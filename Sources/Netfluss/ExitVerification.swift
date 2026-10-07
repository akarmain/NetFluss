// Copyright (C) 2026 Rana GmbH and contributors
// This file is part of Netfluss and is licensed under GPL-3.0-or-later.

import Foundation
import Network
import Darwin

enum ExitVerdict: String {
    case checking, matched, regionOnly, mismatch, unavailable, unconfigured

    var title: String {
        switch self {
        case .checking: return "Checking…"
        case .matched: return "IP matches"
        case .regionOnly: return "Region only · weaker check"
        case .mismatch: return "IP does not match"
        case .unavailable: return "Check unavailable"
        case .unconfigured: return "Configure VPN exit IPs"
        }
    }
}

struct ExitFamilyResult {
    let verdict: ExitVerdict
    let ip: String
    let reason: String
    let checkedAt: Date?

    static let pending = ExitFamilyResult(verdict: .checking, ip: "—", reason: "Waiting for check", checkedAt: nil)
}

/// A literal IP or CIDR. inet_pton rejects hostnames and malformed addresses.
struct ExitIPRange {
    let family: Int32
    let bytes: [UInt8]
    let prefix: Int

    init?(_ text: String) {
        let parts = text.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 1 || parts.count == 2 else { return nil }
        let address = String(parts[0])
        let parsedFamily = address.contains(":") ? AF_INET6 : AF_INET
        let count = parsedFamily == AF_INET6 ? 16 : 4
        var storage = [UInt8](repeating: 0, count: count)
        let result = address.withCString { cString in
            storage.withUnsafeMutableBytes { inet_pton(parsedFamily, cString, $0.baseAddress) }
        }
        guard result == 1 else { return nil }
        let bits = count * 8
        if parts.count == 2 {
            guard let value = Int(parts[1]), value >= 0, value <= bits else { return nil }
            prefix = value
        } else {
            prefix = bits
        }
        bytes = storage
        family = parsedFamily
    }

    func contains(_ ip: String) -> Bool {
        var target = [UInt8](repeating: 0, count: bytes.count)
        let result = ip.withCString { cString in
            target.withUnsafeMutableBytes { inet_pton(family, cString, $0.baseAddress) }
        }
        guard result == 1 else { return false }
        for index in 0..<bytes.count {
            let remaining = prefix - index * 8
            if remaining <= 0 { break }
            let mask: UInt8 = remaining >= 8 ? 255 : UInt8(255 << (8 - remaining))
            if bytes[index] & mask != target[index] & mask { return false }
        }
        return true
    }
}

@MainActor
final class ExitVerification: ObservableObject {
    static let shared = ExitVerification()
    @Published private(set) var ipv4 = ExitFamilyResult.pending
    @Published private(set) var ipv6 = ExitFamilyResult.pending
    @Published private(set) var isChecking = false

    private var timer: Timer?
    private var defaultsObserver: NSObjectProtocol?
    private let pathMonitor = NWPathMonitor()
    private let pathQueue = DispatchQueue(label: "netfluss.exit.path")
    private var generation = 0

    var overall: ExitVerdict {
        if isChecking { return .checking }
        return Self.aggregate(ipv4.verdict, ipv6.verdict)
    }

    static func aggregate(_ four: ExitVerdict, _ six: ExitVerdict) -> ExitVerdict {
        let results = [four, six]
        if results.contains(.mismatch) { return .mismatch }
        if results.allSatisfy({ $0 == .unconfigured || $0 == .unavailable }),
           results.contains(.unconfigured) { return .unconfigured }
        if results.contains(.unavailable) { return .unavailable }
        if results.contains(.unconfigured) { return .unconfigured }
        if results.contains(.regionOnly) { return .regionOnly }
        if results.contains(.matched) { return .matched }
        return .unconfigured
    }

    private init() {
        pathMonitor.pathUpdateHandler = { [weak self] _ in
            Task { @MainActor [weak self] in self?.checkNow() }
        }
        pathMonitor.start(queue: pathQueue)
        defaultsObserver = NotificationCenter.default.addObserver(forName: UserDefaults.didChangeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor [weak self] in self?.schedule() }
        }
        schedule()
        checkNow()
    }

    deinit {
        timer?.invalidate()
        pathMonitor.cancel()
        if let defaultsObserver { NotificationCenter.default.removeObserver(defaultsObserver) }
    }

    func schedule() {
        timer?.invalidate()
        let interval = max(15, min(3600, UserDefaults.standard.double(forKey: "exitCheckInterval")))
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in self?.checkNow() }
        }
    }

    func checkNow() {
        generation += 1
        let current = generation
        let settings = Settings.read()
        isChecking = true
        ipv4 = .pending
        ipv6 = .pending
        Task {
            async let four = Self.check(family: AF_INET, settings: settings)
            async let six = Self.check(family: AF_INET6, settings: settings)
            let results = await (four, six)
            guard current == generation else { return }
            ipv4 = results.0
            ipv6 = results.1
            isChecking = false
        }
    }

    struct Settings {
        let ranges: [ExitIPRange]
        let country: String
        let region: String

        static func read() -> Self {
            let defaults = UserDefaults.standard
            let entries = (defaults.string(forKey: "exitAllowedIPs") ?? "")
                .components(separatedBy: CharacterSet(charactersIn: ",; \n\t"))
                .filter { !$0.isEmpty }
            return Self(ranges: entries.compactMap(ExitIPRange.init),
                        country: (defaults.string(forKey: "exitCountry") ?? "").trimmingCharacters(in: .whitespacesAndNewlines),
                        region: (defaults.string(forKey: "exitRegion") ?? "").trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    static func check(family: Int32, settings: Settings, session: URLSession = .shared) async -> ExitFamilyResult {
        let now = Date()
        let ranges = settings.ranges.filter { $0.family == family }
        let hasRegion = !settings.country.isEmpty || !settings.region.isEmpty
        let endpoint = family == AF_INET ? "https://api.ipify.org?format=json" : "https://api6.ipify.org?format=json"
        do {
            var request = URLRequest(url: URL(string: endpoint)!)
            request.timeoutInterval = 8
            request.cachePolicy = .reloadIgnoringLocalCacheData
            let (data, response) = try await session.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200,
                  let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let ip = object["ip"] as? String,
                  let parsed = ExitIPRange(ip), parsed.family == family, parsed.prefix == (family == AF_INET ? 32 : 128) else {
                throw URLError(.badServerResponse)
            }
            if ranges.isEmpty && !hasRegion {
                return ExitFamilyResult(verdict: .unconfigured, ip: ip, reason: "No address or region configured", checkedAt: now)
            }
            if ranges.contains(where: { $0.contains(ip) }) {
                return ExitFamilyResult(verdict: .matched, ip: ip, reason: "Matches an allowed IP or CIDR range", checkedAt: now)
            }
            if hasRegion {
                do {
                    var geoRequest = URLRequest(url: URL(string: "https://ipwho.is/\(ip)")!)
                    geoRequest.timeoutInterval = 8
                    let (geoData, geoResponse) = try await session.data(for: geoRequest)
                    guard (geoResponse as? HTTPURLResponse)?.statusCode == 200,
                          let geo = try JSONSerialization.jsonObject(with: geoData) as? [String: Any],
                          geo["success"] as? Bool != false else { throw URLError(.badServerResponse) }
                    let country = (geo["country_code"] as? String ?? "").lowercased()
                    let countryName = (geo["country"] as? String ?? "").lowercased()
                    let region = (geo["region"] as? String ?? "").lowercased()
                    let countryOK = settings.country.isEmpty || [country, countryName].contains(settings.country.lowercased())
                    let regionOK = settings.region.isEmpty || region == settings.region.lowercased()
                    if countryOK && regionOK {
                        return ExitFamilyResult(verdict: .regionOnly, ip: ip, reason: "Location matches; IP is not in the allowed list", checkedAt: now)
                    }
                    return ExitFamilyResult(verdict: .mismatch, ip: ip, reason: "Neither IP nor location matches", checkedAt: now)
                } catch {
                    return ExitFamilyResult(verdict: .unavailable, ip: ip, reason: "Location lookup failed: \(error.localizedDescription)", checkedAt: now)
                }
            }
            return ExitFamilyResult(verdict: .mismatch, ip: ip, reason: "IP is not in the allowed list", checkedAt: now)
        } catch {
            return ExitFamilyResult(verdict: .unavailable, ip: "—", reason: "Public IP lookup failed: \(error.localizedDescription)", checkedAt: now)
        }
    }
}

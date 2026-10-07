// Copyright (C) 2026 Rana GmbH and contributors
// This file is part of Netfluss and is licensed under GPL-3.0-or-later.

import SwiftUI

private func verdictColor(_ verdict: ExitVerdict) -> Color {
    switch verdict {
    case .matched: return .green
    case .regionOnly: return .orange
    case .mismatch: return .red
    case .checking, .unavailable, .unconfigured: return .secondary
    }
}

struct ExitVerificationSection: View {
    @ObservedObject private var verification = ExitVerification.shared
    @EnvironmentObject private var speedTestManager: SpeedTestManager

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Image(systemName: "circle.circle")
                    .foregroundStyle(verdictColor(verification.overall))
                Text(verification.overall.title)
                    .font(.system(size: 12, weight: .semibold))
                Spacer()
                Button { SpeedTestWindowController.shared.show(manager: speedTestManager) } label: {
                    Image(systemName: "speedometer")
                }
                .buttonStyle(.plain)
                .help("Speed Test")
                Button { verification.checkNow() } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .buttonStyle(.plain)
                .help("Check now")
            }
            familyRow("IPv4", verification.ipv4)
            familyRow("IPv6", verification.ipv6)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
    }

    private func familyRow(_ name: String, _ result: ExitFamilyResult) -> some View {
        HStack(alignment: .top, spacing: 7) {
            Circle().fill(verdictColor(result.verdict)).frame(width: 7, height: 7).padding(.top, 4)
            VStack(alignment: .leading, spacing: 1) {
                HStack {
                    Text(name).fontWeight(.medium)
                    Spacer()
                    Text(result.verdict.title).foregroundStyle(verdictColor(result.verdict))
                }
                Text(result.ip).textSelection(.enabled)
                Text(result.reason)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if let date = result.checkedAt {
                    Text("Checked \(date.formatted(date: .abbreviated, time: .standard))")
                        .foregroundStyle(.tertiary)
                }
            }
            .font(.system(size: 10))
        }
    }
}

struct ExitVerificationPreferences: View {
    @AppStorage("exitAllowedIPs") private var allowedIPs = ""
    @AppStorage("exitCountry") private var country = ""
    @AppStorage("exitRegion") private var region = ""
    @AppStorage("exitCheckInterval") private var interval = 60.0
    @ObservedObject private var verification = ExitVerification.shared

    private var invalidEntries: [String] {
        allowedIPs.components(separatedBy: CharacterSet(charactersIn: ",; \n\t"))
            .filter { !$0.isEmpty && ExitIPRange($0) == nil }
    }

    var body: some View {
        Section {
            Text("Enter one IPv4 or IPv6 address or CIDR range per line. The list may be empty.")
                .font(.caption).foregroundStyle(.secondary)
            TextEditor(text: $allowedIPs)
                .font(.system(size: 12, design: .monospaced))
                .frame(height: 95)
                .border(.quaternary)
            if !invalidEntries.isEmpty {
                Text("Invalid entries: \(invalidEntries.joined(separator: ", "))")
                    .font(.caption).foregroundStyle(.red)
            }
            TextField("Country code or name (optional)", text: $country)
            TextField("Region name (optional)", text: $region)
            Text("A location match alone is shown in amber; geolocation is less reliable than an IP match.")
                .font(.caption).foregroundStyle(.secondary)
            Picker("Check every", selection: $interval) {
                Text("30 seconds").tag(30.0)
                Text("1 minute").tag(60.0)
                Text("5 minutes").tag(300.0)
                Text("15 minutes").tag(900.0)
            }
            HStack {
                Button("Check now") { verification.checkNow() }
                Spacer()
                Text(verification.overall.title).foregroundStyle(verdictColor(verification.overall))
            }
        } header: {
            Text("VPN exit verification")
        }
    }
}

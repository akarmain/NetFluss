import XCTest
@testable import Netfluss

private final class StubProtocol: URLProtocol {
    static var answer: ((URL) -> (Int, String)?)?
    static var failure: Error?

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func stopLoading() {}

    override func startLoading() {
        if let failure = Self.failure {
            client?.urlProtocol(self, didFailWithError: failure)
            return
        }
        guard let url = request.url, let (code, body) = Self.answer?(url),
              let response = HTTPURLResponse(url: url, statusCode: code, httpVersion: nil, headerFields: nil) else {
            client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse))
            return
        }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
}

@MainActor
final class ExitVerificationTests: XCTestCase {
    private func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubProtocol.self]
        return URLSession(configuration: config)
    }

    private func settings(_ addresses: [String], country: String = "", region: String = "") -> ExitVerification.Settings {
        .init(ranges: addresses.compactMap(ExitIPRange.init), country: country, region: region)
    }

    override func tearDown() {
        StubProtocol.answer = nil
        StubProtocol.failure = nil
        super.tearDown()
    }

    func testCIDRAndInvalidInput() {
        XCTAssertTrue(ExitIPRange("203.0.113.0/24")!.contains("203.0.113.9"))
        XCTAssertFalse(ExitIPRange("203.0.113.0/24")!.contains("203.0.114.9"))
        XCTAssertTrue(ExitIPRange("2001:db8::/32")!.contains("2001:db8:abcd::1"))
        XCTAssertFalse(ExitIPRange("2001:db8::/32")!.contains("2001:db9::1"))
        XCTAssertNil(ExitIPRange("203.0.113.0/33"))
        XCTAssertNil(ExitIPRange("example.com"))
    }

    func testIPv6MismatchCannotBeHiddenByIPv4Match() {
        XCTAssertEqual(ExitVerification.aggregate(.matched, .mismatch), .mismatch)
        XCTAssertEqual(ExitVerification.aggregate(.matched, .unavailable), .unavailable)
        XCTAssertEqual(ExitVerification.aggregate(.matched, .unconfigured), .unconfigured)
        XCTAssertEqual(ExitVerification.aggregate(.matched, .regionOnly), .regionOnly)
        XCTAssertEqual(ExitVerification.aggregate(.matched, .matched), .matched)
    }

    func testMatchedAndMismatchedIPv4() async {
        StubProtocol.answer = { _ in (200, "{\"ip\":\"203.0.113.42\"}") }
        let client = session()
        let good = await ExitVerification.check(family: AF_INET, settings: settings(["203.0.113.0/24", "198.51.100.3"]), session: client)
        XCTAssertEqual(good.verdict, .matched)
        let bad = await ExitVerification.check(family: AF_INET, settings: settings(["198.51.100.0/24"]), session: client)
        XCTAssertEqual(bad.verdict, .mismatch)
    }

    func testRegionOnlyAndIPv6() async {
        StubProtocol.answer = { url in
            if url.host == "ipwho.is" { return (200, "{\"success\":true,\"country_code\":\"DE\",\"region\":\"Berlin\"}") }
            return (200, "{\"ip\":\"2001:db8::42\"}")
        }
        let result = await ExitVerification.check(family: AF_INET6, settings: settings(["2001:db9::/32"], country: "DE", region: "Berlin"), session: session())
        XCTAssertEqual(result.verdict, .regionOnly)
        XCTAssertEqual(result.ip, "2001:db8::42")
    }

    func testNoNetworkAndServiceFailure() async {
        StubProtocol.failure = URLError(.notConnectedToInternet)
        let offline = await ExitVerification.check(family: AF_INET, settings: settings(["203.0.113.1"]), session: session())
        XCTAssertEqual(offline.verdict, .unavailable)
        StubProtocol.failure = nil
        StubProtocol.answer = { _ in (503, "maintenance") }
        let failure = await ExitVerification.check(family: AF_INET, settings: settings(["203.0.113.1"]), session: session())
        XCTAssertEqual(failure.verdict, .unavailable)
    }

    func testEmptySettings() async {
        StubProtocol.answer = { _ in (200, "{\"ip\":\"203.0.113.42\"}") }
        let result = await ExitVerification.check(family: AF_INET, settings: settings([]), session: session())
        XCTAssertEqual(result.verdict, .unconfigured)
        XCTAssertEqual(result.ip, "203.0.113.42")
    }
}

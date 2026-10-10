import Foundation
import XCTest
@testable import PinCCMacPlatform

final class SourceReaderTests: XCTestCase {
    func testExplicitMIMEControlsDocumentRoutingBeforeFilename() {
        for suffix in ["pdf", "docx", "pptx", "xlsx"] {
            XCTAssertNil(SourceReader.documentExtension("text/html", suffix), suffix)
            XCTAssertNil(SourceReader.documentExtension("text/plain", suffix), suffix)
            XCTAssertNil(SourceReader.documentExtension("application/json", suffix), suffix)
            for generic in ["", "application/octet-stream", "binary/octet-stream", "application/x-download", "application/force-download", "application/download"] {
                XCTAssertEqual(SourceReader.documentExtension(generic, suffix), suffix, generic)
            }
        }
        XCTAssertEqual(SourceReader.documentExtension("application/pdf", "html"), "pdf")
        XCTAssertEqual(SourceReader.documentExtension("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ""), "docx")
        XCTAssertEqual(SourceReader.documentExtension("application/vnd.openxmlformats-officedocument.presentationml.presentation", ""), "pptx")
        XCTAssertEqual(SourceReader.documentExtension("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ""), "xlsx")
        XCTAssertNil(SourceReader.documentExtension("application/wordprocessingml-custom", "docx"))
    }

    func testNestedIgnoredElementsAndSelfClosingSVGPreserveArticleText() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("PinCC-SourceTests-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let cases = [
            "<script>if (1 < 2) { hidden(); }</script><p>Article text</p>",
            "<style>.item::before { content: '<'; }</style><p>Article text</p>",
            "<nav><svg></svg>Navigation</nav><p>Article text</p>",
            "<nav><script>if (1 < 2) { hidden(); }</script>Navigation</nav><p>Article text</p>",
            "<footer><nav>Inner navigation</nav>Outer footer</footer><p>Article text</p>",
            "<svg/><p>Article text</p>",
            "<svg viewBox='0 0 10 10' /><p>Article text</p>"
        ]
        for (index, html) in cases.enumerated() {
            let file = root.appendingPathComponent("fixture-\(index).html")
            try Data(html.utf8).write(to: file)
            let content = try await SourceReader.file(file)
            XCTAssertEqual(content.text, "Article text", html)
        }
    }
}

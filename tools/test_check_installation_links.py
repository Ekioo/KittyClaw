import tempfile
import unittest
import urllib.error
from pathlib import Path
from unittest.mock import MagicMock, Mock

from check_installation_links import SOURCES, check_link, collect_links


class InstallationLinkTests(unittest.TestCase):
    def response(self, url, status=200):
        response = MagicMock()
        response.__enter__.return_value = response
        response.status = status
        response.geturl.return_value = url
        return response

    def test_redirect_to_valid_documentation_succeeds(self):
        opener = Mock(return_value=self.response("https://example.com/new"))
        self.assertEqual("https://example.com/new", check_link("https://example.com/old", opener))
        self.assertEqual("GET", opener.call_args.args[0].get_method())

    def test_404_fails_without_retry(self):
        opener = Mock(side_effect=urllib.error.HTTPError("https://example.com", 404, "Not Found", {}, None))
        with self.assertRaises(urllib.error.HTTPError):
            check_link("https://example.com", opener)
        self.assertEqual(1, opener.call_count)

    def test_transient_errors_are_retried(self):
        for code in (429, 503):
            with self.subTest(code=code):
                opener = Mock(side_effect=[urllib.error.HTTPError("https://example.com", code, "Retry", {}, None), self.response("https://example.com")])
                check_link("https://example.com", opener, sleep=Mock())
                self.assertEqual(2, opener.call_count)

    def test_persistent_network_failure_is_bounded(self):
        opener = Mock(side_effect=urllib.error.URLError("unavailable"))
        with self.assertRaises(urllib.error.URLError):
            check_link("https://example.com", opener, sleep=Mock())
        self.assertEqual(3, opener.call_count)

    def test_insecure_redirect_fails(self):
        with self.assertRaises(ValueError):
            check_link("https://example.com", Mock(return_value=self.response("http://example.com")))

    def test_collects_new_links_and_deduplicates_shared_urls(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for source in SOURCES:
                path = root / source
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('href="https://example.com/shared"', encoding="utf-8")
            with (root / SOURCES[0]).open("a") as file:
                file.write(' href="https://example.com/new-provider"')
            links = collect_links(root)
            self.assertEqual(2, len(links))
            self.assertEqual(2, len(links["https://example.com/shared"]))

    def test_missing_links_or_insecure_urls_fail(self):
        for text in ('no links', 'href="http://example.com"'):
            with self.subTest(text=text), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                path = root / SOURCES[0]
                path.parent.mkdir(parents=True)
                path.write_text(text, encoding="utf-8")
                with self.assertRaises(ValueError):
                    collect_links(root)


if __name__ == "__main__":
    unittest.main()

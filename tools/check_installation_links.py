"""Check the installation URLs used by onboarding and global settings."""
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

SOURCES = (
    "KittyClaw.Web/Components/OnboardingGate.razor",
    "KittyClaw.Web/Components/Pages/GlobalSettings.razor",
)


def collect_links(root):
    links = {}
    for source in SOURCES:
        text = (root / source).read_text(encoding="utf-8-sig")
        urls = re.findall(r'"(https?://[^"\s]+)"', text)
        if not urls:
            raise ValueError(f"No installation links found in {source}")
        for url in urls:
            if not url.startswith("https://"):
                raise ValueError(f"Installation link must use HTTPS: {source}: {url}")
            links.setdefault(url, []).append(source)
    return links


def check_link(url, opener=urllib.request.urlopen, sleep=time.sleep):
    # GET follows redirects and works with documentation hosts that reject HEAD.
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "KittyClaw-LinkCheck/1.0"})
            with opener(request, timeout=30) as response:
                status = response.status
                final_url = response.geturl()
                if not 200 <= status < 300:
                    raise ValueError(f"HTTP {status}")
                if not final_url.startswith("https://"):
                    raise ValueError(f"Redirect downgraded HTTPS: {final_url}")
                return final_url
        except urllib.error.HTTPError as error:
            if error.code not in (408, 429) and error.code < 500:
                raise
            if attempt == 2:
                raise
        except (urllib.error.URLError, TimeoutError):
            if attempt == 2:
                raise
        sleep(2 ** attempt)


def main():
    root = Path(__file__).resolve().parent.parent
    failures = []
    for url, sources in collect_links(root).items():
        try:
            final_url = check_link(url)
            print(f"OK {url}" + (f" -> {final_url}" if final_url != url else ""))
        except Exception as error:
            failures.append(f"{url} ({', '.join(sources)}): {error}")
    for failure in failures:
        print(f"FAILED {failure}", file=sys.stderr)
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

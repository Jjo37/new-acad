#!/usr/bin/env python3
"""
Scrape video URLs from Autodesk help documentation pages.

This script can:
- Scrape a single URL
- Scrape multiple URLs from a file
- Resolve JavaScript-backed Civil 3D help URLs to static CloudHelp pages
- Crawl video index pages and inspect their linked video detail pages
- Extract videos from YouTube, Vimeo, and other embedded players
- Save results to JSON or CSV
"""

import argparse
import html
import json
import re
from urllib.parse import parse_qs, urldefrag, urljoin, urlparse

import requests
from bs4 import BeautifulSoup


REQUEST_HEADERS = {
    'User-Agent': (
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) '
        'AppleWebKit/537.36 (KHTML, like Gecko) '
        'Chrome/91.0.4472.124 Safari/537.36'
    )
}

AUTODESK_PRODUCT_GUIDES = {
    'CIV3D': 'Civil3D-UserGuide',
}

AUTODESK_GUID_PATTERN = re.compile(r'^GUID-[A-Z0-9-]+$', re.IGNORECASE)
AUTODESK_GUID_PAGE_PATTERN = re.compile(r'/GUID-[A-Z0-9-]+\.htm$', re.IGNORECASE)


def resolve_autodesk_content_url(url):
    """Resolve a JavaScript-backed Autodesk view URL to static CloudHelp HTML."""
    parsed = urlparse(url)
    path_parts = [part for part in parsed.path.split('/') if part]

    if parsed.netloc.lower() != 'help.autodesk.com' or len(path_parts) < 4:
        return url

    if path_parts[0].lower() != 'view':
        return url

    product, year, language = path_parts[1:4]
    query = {key.lower(): values for key, values in parse_qs(parsed.query).items()}
    guid = query.get('guid', [None])[0]
    guide = AUTODESK_PRODUCT_GUIDES.get(product.upper())

    if not guide or not guid or not AUTODESK_GUID_PATTERN.fullmatch(guid):
        return url

    return (
        f'{parsed.scheme or "https"}://{parsed.netloc}/cloudhelp/'
        f'{year}/{language}/{guide}/files/{guid}.htm'
    )


def extract_linked_help_pages(soup, base_url):
    """Return unique linked Autodesk GUID pages in document order."""
    base_page = urldefrag(base_url).url
    base_host = urlparse(base_page).netloc.lower()
    linked_pages = []
    seen = {base_page}

    for anchor in soup.find_all('a', href=True):
        linked_url = urldefrag(urljoin(base_page, anchor['href'])).url
        parsed = urlparse(linked_url)

        if parsed.netloc.lower() != base_host:
            continue
        if not AUTODESK_GUID_PAGE_PATTERN.search(parsed.path):
            continue
        if linked_url in seen:
            continue

        seen.add(linked_url)
        linked_pages.append(linked_url)

    return linked_pages


def extract_video_urls(html_content, base_url):
    """Extract video URLs from HTML content."""
    video_urls = set()

    def add_video_url(candidate):
        candidate = html.unescape(candidate).strip()
        if candidate:
            video_urls.add(urljoin(base_url, candidate))

    soup = BeautifulSoup(html_content, 'html.parser')

    # Prefer structured media elements so relative source URLs are resolved.
    for media in soup.select('video[src], video source[src]'):
        add_video_url(media.get('src'))

    for media in soup.select('[data-video]'):
        add_video_url(media.get('data-video'))

    # YouTube patterns
    youtube_patterns = [
        r'(?:https?:)?//(?:www\.)?(?:youtube\.com/(?:watch\?v=|embed/|v/)|youtu\.be/)([\w-]{11})',
        r'(?:https?:)?//(?:www\.)?youtube\.com/playlist\?list=([\w-]+)',
    ]

    # Vimeo patterns
    vimeo_patterns = [
        r'(?:https?:)?//(?:www\.)?vimeo\.com/(\d+)',
        r'(?:https?:)?//player\.vimeo\.com/video/(\d+)',
    ]

    # Generic video file patterns
    video_file_patterns = [
        r'(?:https?:)?[^\s"\'<>]+\.(?:mp4|webm|ogg|mov|avi)(?:\?[^\s"\'<>]*)?',
    ]

    # iframe src patterns
    iframe_pattern = r'<iframe[^>]+src=["\']([^"\']+)["\']'

    # video tag patterns
    video_tag_pattern = r'<video[^>]+src=["\']([^"\']+)["\']'

    # Extract from iframes
    for match in re.finditer(iframe_pattern, html_content, re.IGNORECASE):
        src = match.group(1)
        if re.search(r'youtube|youtu\.be|vimeo|video|screencast', src, re.IGNORECASE):
            add_video_url(src)

    # Extract from video tags
    for match in re.finditer(video_tag_pattern, html_content, re.IGNORECASE):
        add_video_url(match.group(1))

    # Extract YouTube URLs
    for pattern in youtube_patterns:
        for match in re.finditer(pattern, html_content, re.IGNORECASE):
            video_id = match.group(1)
            if 'playlist' in pattern:
                video_urls.add(f'https://www.youtube.com/playlist?list={video_id}')
            else:
                video_urls.add(f'https://www.youtube.com/watch?v={video_id}')

    # Extract Vimeo URLs
    for pattern in vimeo_patterns:
        for match in re.finditer(pattern, html_content, re.IGNORECASE):
            video_id = match.group(1)
            video_urls.add(f'https://vimeo.com/{video_id}')

    # Extract direct video file URLs
    for pattern in video_file_patterns:
        for match in re.finditer(pattern, html_content, re.IGNORECASE):
            add_video_url(match.group(0))

    # Extract data-video attributes
    data_video_pattern = r'data-video=["\']([^"\']+)["\']'
    for match in re.finditer(data_video_pattern, html_content, re.IGNORECASE):
        add_video_url(match.group(1))

    return sorted(list(video_urls))


def scrape_page(url, timeout=30, crawl=True, max_pages=100):
    """Scrape a page and optionally crawl linked Autodesk video pages."""
    try:
        resolved_url = resolve_autodesk_content_url(url)
        response = requests.get(resolved_url, headers=REQUEST_HEADERS, timeout=timeout)
        response.raise_for_status()

        soup = BeautifulSoup(response.text, 'html.parser')
        video_urls = set(extract_video_urls(response.text, resolved_url))
        video_pages = []
        crawl_errors = []

        title = ''
        title_tag = soup.find('title')
        if title_tag:
            title = title_tag.get_text().strip()

        if video_urls:
            video_pages.append({
                'url': resolved_url,
                'title': title,
                'video_urls': sorted(video_urls),
            })

        linked_pages = extract_linked_help_pages(soup, resolved_url) if crawl else []
        pages_to_scan = linked_pages[:max_pages]

        for linked_url in pages_to_scan:
            try:
                linked_response = requests.get(
                    linked_url,
                    headers=REQUEST_HEADERS,
                    timeout=timeout,
                )
                linked_response.raise_for_status()
                linked_videos = extract_video_urls(linked_response.text, linked_url)

                if not linked_videos:
                    continue

                linked_soup = BeautifulSoup(linked_response.text, 'html.parser')
                linked_title_tag = linked_soup.find('title')
                linked_title = (
                    linked_title_tag.get_text().strip()
                    if linked_title_tag
                    else ''
                )
                video_pages.append({
                    'url': linked_url,
                    'title': linked_title,
                    'video_urls': linked_videos,
                })
                video_urls.update(linked_videos)
            except requests.RequestException as error:
                crawl_errors.append({
                    'url': linked_url,
                    'error': str(error),
                })

        return {
            'url': url,
            'resolved_url': resolved_url,
            'title': title,
            'video_urls': sorted(video_urls),
            'video_count': len(video_urls),
            'video_pages': video_pages,
            'video_page_count': len(video_pages),
            'linked_pages_scanned': len(pages_to_scan),
            'crawl_errors': crawl_errors,
            'status': 'success'
        }
    except Exception as e:
        return {
            'url': url,
            'title': '',
            'video_urls': [],
            'video_count': 0,
            'video_pages': [],
            'video_page_count': 0,
            'linked_pages_scanned': 0,
            'crawl_errors': [],
            'status': f'error: {str(e)}'
        }


def scrape_urls_from_file(file_path):
    """Read URLs from a file (one per line)."""
    with open(file_path, 'r', encoding='utf-8') as f:
        urls = [line.strip() for line in f if line.strip() and not line.startswith('#')]
    return urls


def save_results(results, output_file, format='json'):
    """Save scraping results to a file."""
    if format == 'json':
        with open(output_file, 'w', encoding='utf-8') as f:
            json.dump(results, f, indent=2)
    elif format == 'csv':
        import csv
        with open(output_file, 'w', encoding='utf-8', newline='') as f:
            writer = csv.writer(f)
            writer.writerow([
                'URL',
                'Resolved URL',
                'Title',
                'Video Page Count',
                'Video Source Count',
                'Video URLs',
                'Status',
            ])
            for result in results:
                writer.writerow([
                    result['url'],
                    result.get('resolved_url', result['url']),
                    result['title'],
                    result.get('video_page_count', 0),
                    result['video_count'],
                    '; '.join(result['video_urls']),
                    result['status']
                ])
    else:
        # Plain text format
        with open(output_file, 'w', encoding='utf-8') as f:
            for result in results:
                f.write(f"URL: {result['url']}\n")
                if result.get('resolved_url') != result['url']:
                    f.write(f"Resolved URL: {result['resolved_url']}\n")
                f.write(f"Title: {result['title']}\n")
                f.write(f"Status: {result['status']}\n")
                f.write(f"Video Pages: {result.get('video_page_count', 0)}\n")
                f.write(f"Video Sources: {result['video_count']}\n")
                if result['video_urls']:
                    f.write("Videos:\n")
                    for video_url in result['video_urls']:
                        f.write(f"  - {video_url}\n")
                f.write("\n" + "-"*80 + "\n\n")


def main():
    parser = argparse.ArgumentParser(
        description='Scrape video URLs from Autodesk help documentation pages'
    )
    parser.add_argument(
        'urls',
        nargs='*',
        help='URLs to scrape (if not provided, use --file)'
    )
    parser.add_argument(
        '--file', '-f',
        help='File containing URLs to scrape (one per line)'
    )
    parser.add_argument(
        '--output', '-o',
        default='autodesk_videos.json',
        help='Output file path (default: autodesk_videos.json)'
    )
    parser.add_argument(
        '--format',
        choices=['json', 'csv', 'txt'],
        default='json',
        help='Output format (default: json)'
    )
    parser.add_argument(
        '--timeout',
        type=int,
        default=30,
        help='Request timeout in seconds (default: 30)'
    )
    parser.add_argument(
        '--no-crawl',
        action='store_true',
        help='Do not follow linked Autodesk GUID pages'
    )
    parser.add_argument(
        '--max-pages',
        type=int,
        default=100,
        help='Maximum linked pages to scan per input URL (default: 100)'
    )

    args = parser.parse_args()

    # Get URLs to scrape
    if args.file:
        urls = scrape_urls_from_file(args.file)
    elif args.urls:
        urls = args.urls
    else:
        parser.error('Either provide URLs as arguments or use --file')

    if args.max_pages < 0:
        parser.error('--max-pages must be zero or greater')

    print(f"Scraping {len(urls)} URL(s)...")

    results = []
    for i, url in enumerate(urls, 1):
        print(f"[{i}/{len(urls)}] Scraping: {url}")
        result = scrape_page(
            url,
            timeout=args.timeout,
            crawl=not args.no_crawl,
            max_pages=args.max_pages,
        )
        results.append(result)

        if result['status'] == 'success':
            print(
                f"  Found {result['video_count']} video source(s) "
                f"across {result['video_page_count']} video page(s)"
            )
            if result['resolved_url'] != result['url']:
                print(f"  Resolved content: {result['resolved_url']}")
            print(f"  Scanned {result['linked_pages_scanned']} linked page(s)")
            for video_url in result['video_urls'][:3]:  # Show first 3
                print(f"    - {video_url}")
            if result['video_count'] > 3:
                print(f"    ... and {result['video_count'] - 3} more")
        else:
            print(f"  Error: {result['status']}")

    # Save results
    save_results(results, args.output, args.format)
    print(f"\nResults saved to: {args.output}")

    # Summary
    total_videos = sum(r['video_count'] for r in results if r['status'] == 'success')
    total_video_pages = sum(
        r.get('video_page_count', 0)
        for r in results
        if r['status'] == 'success'
    )
    successful = sum(1 for r in results if r['status'] == 'success')
    print(
        f"Summary: {successful}/{len(urls)} inputs successful, "
        f"{total_video_pages} video pages and "
        f"{total_videos} video sources found"
    )


if __name__ == '__main__':
    main()

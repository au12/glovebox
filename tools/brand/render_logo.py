"""Render the Glovebox wordmark logos (mark + "Glovebox") with the self-hosted Barlow font.

Usage: python3 tools/brand/render_logo.py wwwroot/lib/fonts wwwroot/defaults
"""
import asyncio, sys, pathlib
from playwright.async_api import async_playwright

FONTS = pathlib.Path(sys.argv[1]).resolve()
DEFAULTS = pathlib.Path(sys.argv[2]).resolve()

def page_html(size, color, mark):
    return f'''<!doctype html><html><head><style>
@font-face{{font-family:'BSC';font-weight:700;src:url('{(FONTS / "barlow-semi-condensed-latin-700-normal.woff2").as_uri()}') format('woff2');}}
html,body{{margin:0;background:transparent}}
#logo{{display:inline-flex;align-items:center;gap:{int(size * .3)}px;padding:{int(size * .12)}px {int(size * .1)}px;font-family:'BSC';font-weight:700;font-size:{size}px;letter-spacing:.01em;color:{color};line-height:1}}
#logo img{{height:{int(size * 1.35)}px;width:auto;display:block}}
</style></head><body><div id="logo"><img src="{(DEFAULTS / mark).as_uri()}"><span>Glovebox</span></div></body></html>'''

async def main():
    async with async_playwright() as p:
        browser = await p.chromium.launch(executable_path="/usr/bin/google-chrome", args=["--no-sandbox", "--allow-file-access-from-files"])
        page = await browser.new_page(device_scale_factor=2)
        jobs = [("glovebox_logo.png", 64, "#16181D", "glovebox_mark.svg"),
                ("glovebox_logo_small.png", 30, "#16181D", "glovebox_mark.svg"),
                ("glovebox_logo_light.png", 64, "#F2F1EC", "glovebox_mark.svg")]
        for name, size, color, mark in jobs:
            html_path = DEFAULTS.parent.parent / "_logo_tmp.html"
            html_path.write_text(page_html(size, color, mark))
            await page.goto(html_path.as_uri())
            await page.evaluate("document.fonts.ready")
            await page.wait_for_timeout(400)
            el = await page.query_selector("#logo")
            await el.screenshot(path=str(DEFAULTS / name), omit_background=True)
            html_path.unlink()
            print("saved", name)
        await browser.close()

asyncio.run(main())

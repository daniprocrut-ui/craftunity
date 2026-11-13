import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEMO = os.path.join(ROOT, "docs", "demo")


def main() -> int:
    css = open(os.path.join(DEMO, "demo.css"), encoding="utf-8").read()
    js = open(os.path.join(DEMO, "demo.js"), encoding="utf-8").read()
    html = open(os.path.join(DEMO, "index.html"), encoding="utf-8").read()

    guard = ".view:not(.active){display:none}"
    out = html.replace(
        '<link rel="stylesheet" href="demo.css">',
        "<style>\n" + css + "\n" + guard + "\n</style>",
    )
    out = out.replace('<script src="demo.js"></script>', "<script>\n" + js + "\n</script>")

    if 'href="demo.css"' in out or 'src="demo.js"' in out:
        print("FAIL: ссылки на внешние файлы остались")
        return 1

    with open(os.path.join(ROOT, "test", "v1537", "lite.html"), "w", encoding="utf-8", newline="") as f:
        f.write(out)
    with open(os.path.join(DEMO, "lite.html"), "w", encoding="utf-8", newline="") as f:
        f.write(out)

    print("OK", len(out))
    return 0


if __name__ == "__main__":
    sys.exit(main())

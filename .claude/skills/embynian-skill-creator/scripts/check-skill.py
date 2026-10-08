#!/usr/bin/env python3
"""EmbyNian 项目技能的结构检查（辅助工具，不是第五道闸门）。

只做机械检查，不判断内容对不对：

  - SKILL.md 存在，frontmatter 能解析
  - name 与目录名一致、kebab-case、长度合规
  - description 存在、不含尖括号、长度合规
  - 正文行数（超过阈值提醒分层）
  - 正文里的相对链接可达
  - CLAUDE.md 的技能清单里是否登记

用法：

  python check-skill.py --all
  python check-skill.py .claude/skills/embynian-playback [更多目录 …]
  python check-skill.py --all --root <仓库根>

退出码：有 error 返回 1；只有 warning 或全部通过返回 0。
只依赖标准库；frontmatter 按本仓实际的 `key: "value"` 单行写法解析，
多行或嵌套值会被记为 warning 而不是尝试完整实现 YAML。
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

MAX_NAME = 64
MAX_DESCRIPTION = 1024
MAX_BODY_LINES = 500

# 本仓与上游允许的 frontmatter 键。出现别的键多半是写错了或抄了别的生态的字段。
ALLOWED_KEYS = {
    "name",
    "description",
    "license",
    "allowed-tools",
    "metadata",
    "compatibility",
    "description_zh",
    "description_en",
}

KEBAB_RE = re.compile(r"^[a-z0-9-]+$")
KEY_RE = re.compile(r"^([A-Za-z0-9_-]+):[ \t]?(.*)$")
LINK_RE = re.compile(r"\[[^\]]*\]\(([^)\s]+)")
FENCE_RE = re.compile(r"^[ \t]*(?:```|~~~)", re.MULTILINE)
SCHEME_RE = re.compile(r"^[A-Za-z][A-Za-z0-9+.-]*:")


class Report:
    def __init__(self, name: str) -> None:
        self.name = name
        self.errors: list[str] = []
        self.warnings: list[str] = []

    def error(self, message: str) -> None:
        self.errors.append(message)

    def warn(self, message: str) -> None:
        self.warnings.append(message)

    @property
    def ok(self) -> bool:
        return not self.errors


def find_root(start: Path) -> Path | None:
    """从 start 往上找带 CLAUDE.md 与 .claude/skills 的仓库根。"""
    for candidate in (start, *start.parents):
        if (candidate / "CLAUDE.md").is_file() and (candidate / ".claude" / "skills").is_dir():
            return candidate
    return None


def split_frontmatter(text: str) -> tuple[str | None, str]:
    """返回 (frontmatter 文本, 正文)。没有合法 frontmatter 时前者为 None。"""
    if not text.startswith("---"):
        return None, text
    match = re.match(r"^---[ \t]*\r?\n(.*?)\r?\n---[ \t]*(?:\r?\n|$)", text, re.DOTALL)
    if not match:
        return None, text
    return match.group(1), text[match.end():]


def parse_scalar(raw: str) -> str:
    raw = raw.strip()
    if len(raw) >= 2 and raw[0] == raw[-1] and raw[0] in "\"'":
        quote = raw[0]
        inner = raw[1:-1]
        return inner.replace("\\" + quote, quote).replace("\\\\", "\\")
    return raw


def parse_frontmatter(block: str, report: Report) -> dict[str, str]:
    fields: dict[str, str] = {}
    for lineno, line in enumerate(block.splitlines(), start=2):
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        match = KEY_RE.match(line)
        if not match:
            report.warn(
                f"frontmatter 第 {lineno} 行不是单行 `key: value`（多行或嵌套值本检查不支持）："
                f"{line.strip()[:60]!r}"
            )
            continue
        key, value = match.group(1), parse_scalar(match.group(2))
        if key in fields:
            report.warn(f"frontmatter 里 `{key}` 出现多次，只取最后一条")
        fields[key] = value
    return fields


def check_frontmatter(fields: dict[str, str], skill_dir: Path, report: Report) -> None:
    for key in sorted(set(fields) - ALLOWED_KEYS):
        report.warn(f"frontmatter 出现非预期键 `{key}`")

    name = fields.get("name")
    if name is None:
        report.error("frontmatter 缺 `name`")
    elif not name:
        report.error("`name` 为空")
    else:
        if not KEBAB_RE.match(name):
            report.error(f"`name` 必须是 kebab-case（小写字母、数字、连字符）：{name!r}")
        if name.startswith("-") or name.endswith("-") or "--" in name:
            report.error(f"`name` 不能以连字符开头/结尾，也不能有连续连字符：{name!r}")
        if len(name) > MAX_NAME:
            report.error(f"`name` 过长（{len(name)} 字符，上限 {MAX_NAME}）")
        if name != skill_dir.name:
            report.error(f"`name` 与目录名不一致：name={name!r}，目录={skill_dir.name!r}")

    description = fields.get("description")
    if description is None:
        report.error("frontmatter 缺 `description`（这是唯一的触发机制）")
    elif not description:
        report.error("`description` 为空")
    else:
        if "<" in description or ">" in description:
            report.error("`description` 不能包含尖括号 < 或 >")
        if len(description) > MAX_DESCRIPTION:
            report.error(f"`description` 过长（{len(description)} 字符，上限 {MAX_DESCRIPTION}）")
        elif len(description) < 40:
            report.warn(f"`description` 偏短（{len(description)} 字符），可能缺少触发锚点")


def strip_fenced_code(text: str) -> str:
    """去掉围栏代码块，避免把代码里的方括号当链接。"""
    out: list[str] = []
    inside = False
    for line in text.splitlines():
        if FENCE_RE.match(line):
            inside = not inside
            continue
        if not inside:
            out.append(line)
    return "\n".join(out)


def check_links(body: str, skill_dir: Path, report: Report) -> None:
    for target in LINK_RE.findall(strip_fenced_code(body)):
        if target.startswith("#") or SCHEME_RE.match(target):
            continue
        path_part = target.split("#", 1)[0].strip()
        if not path_part:
            continue
        resolved = (skill_dir / path_part).resolve()
        if not resolved.exists():
            report.error(f"正文里的相对链接打不开：`{target}`（解析为 {resolved}）")


def check_registration(root: Path | None, name: str, report: Report) -> None:
    if root is None:
        report.warn("找不到仓库根，跳过 CLAUDE.md 登记检查")
        return
    claude_md = root / "CLAUDE.md"
    expected = f".claude/skills/{name}/SKILL.md"
    if expected not in claude_md.read_text(encoding="utf-8"):
        report.error(f"CLAUDE.md 的技能清单里没有登记 `{expected}`；漏登记等于技能不存在")


def check_skill(skill_dir: Path, root: Path | None) -> Report:
    report = Report(skill_dir.name)
    skill_md = skill_dir / "SKILL.md"
    if not skill_md.is_file():
        report.error(f"{skill_md} 不存在")
        return report

    text = skill_md.read_text(encoding="utf-8")
    frontmatter, body = split_frontmatter(text)
    if frontmatter is None:
        report.error("SKILL.md 开头没有合法的 `---` frontmatter 块")
    else:
        check_frontmatter(parse_frontmatter(frontmatter, report), skill_dir, report)

    body_lines = len(body.splitlines())
    total_lines = len(text.splitlines())
    if body_lines > MAX_BODY_LINES:
        report.warn(
            f"正文 {body_lines} 行，超过 {MAX_BODY_LINES} 行的分层阈值；"
            f"把细节移到 references/ 并在正文写清什么时候去读"
        )

    check_links(body, skill_dir, report)
    check_registration(root, skill_dir.name, report)

    print(f"{'FAIL' if not report.ok else 'ok  '}  {skill_dir.name}  "
          f"(frontmatter {len(frontmatter or '')} 字符, 正文 {body_lines} 行, 全文 {total_lines} 行)")
    for message in report.errors:
        print(f"        error   {message}")
    for message in report.warnings:
        print(f"        warn    {message}")
    return report


def collect_targets(args: argparse.Namespace, root: Path | None) -> list[Path]:
    if args.skill_dirs:
        return [Path(p) for p in args.skill_dirs]
    if root is None:
        print("error: 找不到仓库根（需要同时存在 CLAUDE.md 与 .claude/skills），请用 --root 指定")
        sys.exit(1)
    skills_root = root / ".claude" / "skills"
    return sorted(p for p in skills_root.iterdir() if (p / "SKILL.md").is_file())


def main() -> int:
    parser = argparse.ArgumentParser(description="检查 EmbyNian 项目技能的结构一致性")
    parser.add_argument("skill_dirs", nargs="*", help="要检查的技能目录；省略时配合 --all 检查全部")
    parser.add_argument("--all", action="store_true", help="检查 .claude/skills 下的全部技能")
    parser.add_argument("--root", help="仓库根；默认从当前目录往上找")
    args = parser.parse_args()

    if not args.skill_dirs and not args.all:
        parser.error("至少要给一个技能目录，或用 --all")

    root = Path(args.root).resolve() if args.root else find_root(Path.cwd().resolve())
    targets = collect_targets(args, root)

    if not targets:
        print("没有找到任何带 SKILL.md 的技能目录")
        return 1

    reports = [check_skill(d.resolve(), root) for d in targets]
    failed = [r for r in reports if not r.ok]
    warned = [r for r in reports if r.ok and r.warnings]
    print(
        f"\n{len(reports)} 个技能：{len(failed)} 个 error，"
        f"{len(warned)} 个仅 warning，{len(reports) - len(failed) - len(warned)} 个干净"
    )
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())

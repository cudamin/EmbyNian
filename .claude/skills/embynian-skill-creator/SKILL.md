---
name: "embynian-skill-creator"
description: "在 EmbyNian 仓库里新建、改写、拆分、合并或退役 .claude/skills 下的项目技能：动手前查重与判断该不该新建、frontmatter 与 description 触发词的写法、正文只记什么不记什么、references/scripts 分层、CLAUDE.md 技能清单登记，以及用四道闸门替代上游评测框架的验收方式。Use when creating, editing, splitting, merging or retiring a skill under .claude/skills, when asked 写个技能 / 改技能 / 这个技能该不该拆 / 技能没触发, or when adding a skill that CLAUDE.md should list; policy lives in CLAUDE.md, gates are embynian-verification."
---

# EmbyNian — 项目技能的创作与迭代

规则归 [CLAUDE.md](../../../CLAUDE.md)：闸门、授权、凭据、协作与交付以它为准。本技能只讲"怎么在本仓写、改、退一个技能"，不另立验证或发布政策；验什么、读数怎么判读见 `embynian-verification`。

`.claude/skills/` 下的技能随代码入库，和源码一起被评审、一起被回退。它们是写给"下一个没读过这段历史的会话"的：把一处会反复误判的地方压成一份能直接读的文件。所以判断标准不是"写得全不全"，而是**不看这一份，下一个会话会不会在同一个地方再踩一次**。

## 先判断该不该动技能

| 情况 | 动作 |
| --- | --- |
| 一处会反复误判的地形（协议面、管线、设置落地、证据判读） | 新建技能 |
| 已有技能覆盖了这片地形，只是漏了某个坑 | 补进已有技能，别新开 |
| 一个技能里塞了两个互不相干的地形 | 拆 |
| 两个技能描述同一片地形，读哪个都行 | 合并 |
| 技能描述的对象已经没了（文件删了、功能退役） | 退役，并摘掉 CLAUDE.md 的登记 |

**一个技能 = 一片地形，不是一个功能点。** 现有技能都对应"改一处会牵连一片"的区域。按功能点碎切会得到一堆互相引用的短文件，读的时候还是得全打开，等于没分层。

## 动手前先查重

本地和远程都看一遍，避免造出第三个讲同一件事的技能。

```bash
# 本地
ls .claude/skills
# 远程（只读）
git fetch origin master
git ls-tree -r --name-only origin/master -- .claude/skills | grep 'SKILL.md$'
```

读远程单个技能时，Git Bash 会把 `rev:path` 里的冒号当路径分隔符吃掉，必须关掉路径转换：

```bash
MSYS_NO_PATHCONV=1 git show origin/master:.claude/skills/embynian-verification/SKILL.md
```

远程只用于查重和比对历史，**改动一律落在当前工作树**。工作树里可能有尚未推送的技能（本机现在就有 `embynian-emby-api`），所以远程列表是不全的，别拿它当"本仓一共有几个技能"的答案。

再挑 2～3 个同类技能通读正文，新写的这一份要在语气、密度和分层上和它们一致。

## frontmatter

```yaml
---
name: "embynian-<地形>"
description: "…"
---
```

- **目录名 = `name`**，kebab-case。领域技能沿用 `embynian-` 前缀；跨领域的通用方法（如 `mpv-shader-quality`）可以不带前缀，但要有区分度。本仓还有一个同名的用户级 `skill-creator` 插件，所以这里带前缀不只是习惯，也是避免撞名。
- **`description` 是唯一的触发机制。** 模型只看得到 name + description 这一行；正文要等它决定读这个技能之后才进上下文。所以"管什么"和"什么时候用"都得写在这一行里，正文里再写一遍没有用。

description 要同时给出三样东西：

1. **管什么地形** —— 一句话说清覆盖范围，含边界（"播放地形是 embynian-playback，设置页的坑是 embynian-winui-shell"）。
2. **什么时候用** —— 具体到文件路径、类名、控件名、用户会说的话。`Use when touching Core/Emby/EmbyHttp.cs, EmbyUrl.cs, …` 比"用于 Emby 相关开发"有效得多，因为触发发生在模型还没读正文的时候。
3. **别处管什么** —— 指向相邻技能，防止两处讲同一件事。

中文描述 + 一句英文 `Use when …` 是现有惯例（英文那半句负责接住英文提问）；全中文或全英文的也有，不必强行统一，但同一个技能内部要一致。

两个常见错误：把正文的目录结构抄进 description（太长，且不提供触发信息）；写成"帮助用户做 X"这种没有锚点的句子（模型认不出该在什么时候读它）。

## 正文

开头一句指回 [CLAUDE.md](../../../CLAUDE.md)，说明本技能只负责什么、政策在哪。现有技能都这么做，因为规则只有一份权威出处。

正文**只写"不看就会再踩"的东西**：

- 反直觉的事实（"客户端不调用 `/Items/{Id}/PlaybackInfo`"、"规格里没有这个接口，是缺口不是笔误"）
- 踩过的坑和它的判据（"TEMP 指到仓库内才全绿；同一 exe 换目录就正常 → 与代码无关"）
- 边界与不能做的事（"三条探针固定走集成管线，不覆盖独占窗口"）
- 表格化的对照（文件名 → 管什么；客户端参数 → 规格参数）

正文**不写**：

- 源码、项目文件、脚本帮助里能直接查到的事实（版本号、开关列表、当前文件数）——写进去就会过期，而且读者本来就会去看源文件。技能里写"从源码取"比抄一份准。
- 别的技能已经讲过的地形。
- 升级、验证、安全、发布政策——那些归 CLAUDE.md。
- 本机 `work/` 下的实验脚本当作前提——`work/` 被 gitignore，干净检出的会话没有它们。

语言用中文大白话，像跟同事交接那样讲清"为什么"。现有技能几乎不用全大写的 MUST/ALWAYS，而是把原因讲出来让读者自己判断，这样遇到没写到的情形也能推。正文不贴大段代码，要指代码就给 `文件:行号`。

**分层**：`SKILL.md` 保持在 500 行以内；细节进 `references/`（正文里明确写"什么时候去读哪一个"）；会被反复重写的确定性脚本进 `scripts/`。判断脚本该不该收进来：如果几个不同的任务里都在现写同一个脚本，那它属于 `scripts/`。

## 登记

新技能、改名或退役都要同步 [CLAUDE.md](../../../CLAUDE.md) 的"按任务读取随代码入库的技能"清单——那份清单是技能被发现的地方，漏登记等于技能不存在。加一行，写清相对路径和一句话职责。

技能内容改了、清单里的职责描述不再准确时一起改。清单和技能不一致，比没有清单更坏。

## 验收

**本仓不跑上游 skill-creator 的评测框架**（`evals/`、`benchmark.json`、eval viewer、description 优化循环）：那套依赖 `claude -p` 子进程和 baseline 对比，本机没有，而且技能在这里的成败由真闸门和真实读数判定，不由评分脚本判定。要的是同一个东西——"改完比改前更不容易踩坑"——只是证据来自本仓。

按 [CLAUDE.md 的改动类型表](../../../CLAUDE.md#四道闸门与日常交付)取并集。改技能通常是纯文档改动，但要先确认这次改动里没夹带可执行内容：

- 技能里写到的路径、类名、命令、开关，逐条核对当前工作树里确实存在。技能过期最典型的样子，就是指向一个已经改名或删掉的文件。
- 正文里的相对链接（`../../../CLAUDE.md`、`references/xxx.md`、相邻技能）要能打开。
- 引用的读数用本轮工具输出，不用历史记录——PROGRESS.md 里的历史诊断描述的是它自己那一轮，不构成现行事实。
- 新增或改写 `scripts/` 下的脚本时，脚本本身算"脚本改动"：按表的对应行验，并且**手工跑一遍它的成功路径和失败路径**（造一个缺 frontmatter 或链接失效的临时技能目录），别只看它通过。
- 同一轮里如果还改了产品代码或脚本，取并集，不能用"只是文档"盖过去。

辅助检查（**不是第五道闸门**，不写进 CLAUDE.md 的闸门表）：

```bash
python .claude/skills/embynian-skill-creator/scripts/check-skill.py --all
python .claude/skills/embynian-skill-creator/scripts/check-skill.py .claude/skills/embynian-playback
```

它查 frontmatter 能否解析、`name` 与目录是否一致、description 长度与禁用字符、正文行数、相对链接是否可达、CLAUDE.md 是否登记。只做机械检查，不判断内容对不对。

## 迭代

技能写完不算完，要拿真实任务试一次：让一个没读过这段历史的会话（或下一轮的你）按技能做一遍，然后**读过程而不是只看结果**——它有没有绕路、有没有去翻技能里没提但本该提的文件、有没有被一段没用的内容带偏。技能让人多做无用功，就删掉那一段。

判断标准始终是同一个：不读这一份，会不会在同一个地方再踩。

三个具体信号：

- 同一段解释在几个技能里重复 → 提到一处，其余指过来。
- 技能里的命令每次都要临时改参数才能用 → 补全参数说明，或把脚本收进 `scripts/`。
- description 写了但没触发 → 检查是不是缺了具体锚点（文件路径、类名、用户会说的词），而不是加更多形容词。

## 交付

技能随代码入库，走 [CLAUDE.md 的 Git 规则](../../../CLAUDE.md#git-与协作)：**只有用户明确要求才提交**；源文件用 LF；中文提交正文加 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`；提交后推 `origin master`。

远程列表来自 `origin/master`，所以本地退役或改名之后不推，远程就一直列着那个已经不存在的技能——下一轮查重会被它误导。

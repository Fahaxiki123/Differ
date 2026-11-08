"""Summarize preserved measurements; never infer a pass from missing observations."""
import argparse
import json
import os
import statistics
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
local_data = Path(os.environ.get("LOCALAPPDATA", Path.home() / ".local" / "share"))
parser.add_argument("--evidence", required=True, type=Path,
                    help="Directory containing before/after.json and ui-before/ui-after.json.")
parser.add_argument("--output", type=Path,
                    default=local_data / "Differ" / "WorkspaceMaintenance" / "performance" / "PERFORMANCE_RESULTS.md",
                    help="Report file (default: user-local Differ/WorkspaceMaintenance/performance/PERFORMANCE_RESULTS.md).")
args = parser.parse_args()
evidence = args.evidence.expanduser().resolve()
output = args.output.expanduser().resolve()
before = json.loads((evidence / "before.json").read_text(encoding="utf-8-sig"))
after = json.loads((evidence / "after.json").read_text(encoding="utf-8-sig"))
ui_before = json.loads((evidence / "ui-before.json").read_text(encoding="utf-8-sig"))
ui_after = json.loads((evidence / "ui-after.json").read_text(encoding="utf-8-sig"))
out = ["## 最终固定样本数据", "", "单位为毫秒。单元格为暖运行中位数（最小–最大），各3次；首次调用与全部原始数据在JSON中。", "",
       "| 样本 | 适配器/权威比较 前→后 | 展示模型 前→后 | 适配器托管分配 MiB 前→后 | 差异数 前→后 |", "|---|---|---|---|---|"]

def summarize(rows, key):
    values = [r[key] for r in rows]
    return f"{statistics.median(values):.2f} ({min(values):.2f}–{max(values):.2f})"

for name in dict.fromkeys(r["name"] for r in before):
    b = [r for r in before if r["name"] == name and r["run"] > 0]
    a = [r for r in after if r["name"] == name and r["run"] > 0]
    assert len(a) == len(b) == 3
    assert a[0]["oldSha256"] == b[0]["oldSha256"] and a[0]["newSha256"] == b[0]["newSha256"]
    out.append(f"| {name} | {summarize(b, 'adapterMs')} → {summarize(a, 'adapterMs')} | "
               f"{summarize(b, 'presentationMs')} → {summarize(a, 'presentationMs')} | "
               f"{statistics.median(r['adapterAllocatedBytes'] for r in b)/1048576:.1f} → "
               f"{statistics.median(r['adapterAllocatedBytes'] for r in a)/1048576:.1f} | {b[0]['changed']} → {a[0]['changed']} |")
out += ["", "文本修后为真实编辑会话 Open→有界 EditorDiffEngine→权威文档；新增会话、指纹与版本成本也纳入，不能只比较旧 TextDiffAdapter 路径。Word/Excel仍使用同适配器入口。", "",
        "## Windows WPF 交互对照", "", "每项100次。小步滚动为每次3行，可见格选择与远距离跳转分别报告。计时至布局空闲，不代表物理屏幕帧率。", "",
        "| 操作 | p50 前→后 | p95 前→后 | 最大 前→后 | 修后 p95≤100ms | 修后布局p95≤16.67ms |", "|---|---|---|---|---|---|"]
for b in ui_before["Operations"]:
    a = next(r for r in ui_after["Operations"] if r["Name"] == b["Name"])
    assert len(a["SamplesMs"]) == len(b["SamplesMs"]) == 100
    bs, ars = b["Statistics"], a["Statistics"]
    cells = [f"{bs[k]:.2f} → {ars[k]:.2f}" for k in ["P50Ms", "P95Ms", "MaxMs"]]
    out.append(f"| {b['Name']} | {' | '.join(cells)} | {'是' if a['Passed100MsP95'] else '未达标'} | {'是' if a['Meets60HzLayoutBudgetP95'] else '未达标'} |")
out += ["", "| 首次可操作布局 | 修前 | 修后 |", "|---|---|---|"]
for b in ui_before["Inputs"]:
    a = next(r for r in ui_after["Inputs"] if r["Name"] == b["Name"])
    assert a["BaseSha256"] == b["BaseSha256"] and a["MineSha256"] == b["MineSha256"]
    out.append(f"| {b['Name']} | {b['FirstOperableLayoutMs']:.1f}ms | {a['FirstOperableLayoutMs']:.1f}ms |")
out += ["", f"UI基准进程累计峰值工作集：{ui_before['PeakWorkingSetBytes']/1048576:.1f} → {ui_after['PeakWorkingSetBytes']/1048576:.1f} MiB。两次输入完整性检查均为 {ui_before['InputsUnchanged'] and ui_after['InputsUnchanged']}。", ""]
editor_file = evidence / "screenshots/code-editor-interaction-samples.json"
if editor_file.exists():
    editor = json.loads(editor_file.read_text(encoding="utf-8-sig"))
    out += ["## 中型文本编辑器交互", "", "Windows前台10k行、本地WebView2，100次公开executeEdits+40次采纳/撤销/重做。rAF反馈不等于物理键盘或IME组合延迟；权威结果就绪含100ms合批等待。", "",
            "| 操作 | 样本数 | 输入后rAF p95 | 当前权威结果渲染 p50 / p95 / 最大 |", "|---|---|---|---|"]
    for operation in sorted({r["Operation"] for r in editor["Samples"]}):
        rows = [r for r in editor["Samples"] if r["Operation"] == operation]
        frames = sorted(r["InputFrameMs"] for r in rows)
        times = sorted(r["AuthorityRenderedMs"] for r in rows)
        p95 = max(0, int(len(rows) * .95 + .9999) - 1)
        out.append(f"| {operation} | {len(rows)} | {frames[p95]:.2f}ms | {statistics.median(times):.2f} / {times[p95]:.2f} / {times[-1]:.2f}ms |")
    out += ["", "宿主及WebView2子进程工作集见原始JSON；已退出进程记录0，不能据此认定没有长期泄漏。三轮双会话循环已验证模型数归回2和控件释放，未做数小时压力运行。", ""]
out += ["## 未达标与测量局限", "", "- 超过目标的远跳、布局帧预算及首次加载仍按表中实际数字标记；不宣称所有大表流畅或达到60Hz。",
        "- provisional目录保留探索记录。最初UI脚本误将行号沟槽当业务单元格，导致该探索进程停滞，已中止；最终两版使用同一修正脚本、业务格动作、100样本和20秒watchdog。",
        "- Word曾实测出现富文本控件导致的1秒尖峰，随后改为普通段落轻量呈现、非零缩进/主动字符选择按需富文本；最终UI表是修正后重测，旧回退数据仍保留。",
        "- 当前无未安装Runtime且断网的干净Windows环境；物理显示帧率、完整系统DPI矩阵与原始用户案例未由这些基准证明。", ""]
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text("\n".join(out), encoding="utf-8")
print(f"Wrote {output}; all paired fixture hashes match.")

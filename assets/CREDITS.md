# 素材清单与授权

> 规则：**每个素材包在应用前都需要人工过目。** 本文件记录来源、授权与用途。
> 一旦采用任何需要署名的素材，此文件就是合规凭证 —— 所以从第一天起维护。
>
> ⚠ **本仓库只保留「当前真正在用」的素材。** 已淘汰的备选包不再随仓库分发
> （它们的评测与许可记录留在本地的 `docs/美术资源候选清单.md`，未纳入版本控制）。

---

## 已入库

### ⭐ sierrassets — Pixel Art Automation Tileset（**当前在用**）

| 项 | 值 |
|---|---|
| 作者 | sierrassets |
| 来源 | https://sierrassets.itch.io/automation-tileset |
| 定价 | Name your own price（可 0 元，**建议付费支持**） |
| 评分 | 4.8 / 5（8 评） |
| **授权** | **自定义许可，不是 CC0** —— 见下方原文 |
| 商用 | **允许**（商业/非商业游戏均可） |
| 限制 | **不得转售素材包本身**（含修改后转售），不得制作印有该素材的 T 恤/杯子等商品 |
| 署名 | 许可原文未强制要求（仍建议署名） |
| 规格 | **32×32 px**；`automation spritesheet.png` = 320×3776 = **10 列 × 118 行** = 1180 格 |
| 落点 | `assets/automation pack/automation spritesheet.png`（**只保留这一张，即实际被引用的那张**） |

**许可原文**（itch 页面「License in normalspeak」）：

> You may use this asset pack to develop any commercial/ non-commercial game. You may not re-sell this asset pack (not even with adjustments) and you may not sell for example t-shirts, cups et cetera that feature this asset pack.

> ⚠ **⚠ 注意「不得转售」这条对本仓库的意义。**
> 许可授予的是「**用它做游戏**」，并没有授予**再分发素材包本身**。
> 因此本仓库**只保留渲染所必需的那一张图集**，随包附带的 `.aseprite` 源文件与
> 其余同包文件都没有入库 —— 少一层「替作者分发原始素材包」的争议。
>
> ⚠ 另：**下载包里还有一份 `license itch io.txt`（2.9 KB），那是权威文本，但它不在本仓库里。**
> 上表「许可原文」是从 itch 页面逐字摘的，**不是**那份权威文件的全文。
> 一旦将来对许可有争议，那份文件才是凭证 —— 请把它保管在自己的下载包目录里。

**包内已有**（逐格目视 + 像素哈希核对）：

| 区域 | 内容 |
|---|---|
| 行 0–3 | **物品图标**：33 个不重复的小物件（方块/齿轮/金币/矿石/板/箱…） |
| 行 5–13 | **传送带**：4 向连通集（直线/拐角/T/十字，各方向齐全） |
| 行 14 | 方向箭头与输入/输出标记 |
| 行 15–21 | **机器：3×2 瓦片（96×64），4 帧运转动画**，帧间纵向间隔 2 行；3 种机器并排 |
| 行 23+ | 另一组更大的机器（含彩色指示面板，多帧动画） |

**两个决定性结论**（都影响架构，不是细节）：

1. **传送带是静态瓦片，没有动画帧。** 行 5 的 10 张瓦片像素**完全相同**（已用 SHA1 逐格确认），整条带子靠平铺同一张瓦片构成。
   ⇒ **流动感必须靠滚动 UV**，不是靠换帧。这正好补上了我们方案里「三层分离」缺的中间层：
   **静态带面 + 滚动 UV + 物品实例**。相位由模拟按「带子实际走了多远」算出，画面速度与逻辑速度天然一致。
2. **机器有真正的动画帧**（4 帧运转），所以这台机器**可以**做视觉反馈。
   本项目让动画**只在机器处于 Working 时推进**，空闲/堵塞时停在第 0 帧。

**图集映射**（全部逐格核对，写在 `ForgeFlow.Sim/FactorySim.Atlas.cs` 的图集映射区）：

| 用途 | 图集位置 |
|---|---|
| 直线水平带面 | (行 5, 列 0) —— 单张瓦片，可无缝平铺 |
| 机器帧 f 的左上角 | (行 15 + f×2, 列 0)，f ∈ 0..3；机器占 3×2 瓦片 |
| 物品图标格 | 见 `FactorySim.Atlas.cs` 的 `ItemIconCells`（12 个候选）。内容表的 7 种物品各自从里面挑 —— `ContentDb.DefaultItems` 的 `IconCell` **只能从 `ItemIconCells` 里挑**，这是探针盯着的等式 |

---

## 待人工确认（尚未下载）

评测记录与逐条许可核对见本地的 `docs/美术资源候选清单.md`（**未纳入本仓库**）。
其中与本包互补的 CC0 选项：

- 0x72 16×16 Industrial Tileset（CC-0，16×16）
- OGA factory tileset（CC0）

**排除项**（授权风险，不要用）：

- OGA Conveyor Belt Spriteset —— CC-BY-SA 4.0，传染性
- LPC 系列 —— CC-BY-SA 3.0 + GPLv3 双授权
- game-icons.net —— 同包内混许可，需逐作者筛选
- TexturePacker 免费版 —— 明确「Use in commercial projects — No」，**不能**用于会发布的构建

---

## 工程约束

- **所有帧必须等尺寸**：MultiMesh 逐实例 UV 偏移的 shader 要求如此。当前素材是 32×32，统一即可
- 材质要挂在 `MultiMeshInstance2D` **节点**上（挂到 mesh 上无效）
- 图集导入必须用 **Lossless** 压缩（像素画），过滤设 Nearest，禁用 VRAM Compressed
- MultiMesh **没有逐实例 Z 排序**，需要靠分层节点解决

# MuseBox

<p align="center">
  <img src="Assets/app-icon-preview.png" width="112" alt="MuseBox 应用图标" />
</p>

<p align="center"><strong>收集灵感，整理素材，在自由画板上展开想法。</strong></p>

<p align="center">
  <a href="https://github.com/Xxuebi/MuseBox/releases/latest"><img alt="GitHub Release" src="https://img.shields.io/github/v/release/Xxuebi/MuseBox?display_name=tag&sort=semver"></a>
  <img alt="Windows" src="https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?logo=windows&logoColor=white">
  <a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-green"></a>
</p>

MuseBox 是一款 Windows 本地图片收集与视觉整理工具。用小窗将截图和图片收进不同抽屉，再从左侧素材区拖入自由画板，搭配文字、绘制、组合与图层，整理参考图、设计资料或日常灵感。

无需注册账号，素材库和设置保存在本机。画板可以保存为可继续编辑的 `.mubo` 文件，也可以导出原图或合成图片。

## 下载与安装

当前版本：**1.2.0** · [查看全部发行版本](https://github.com/Xxuebi/MuseBox/releases)

| 版本 | 下载 | 使用方式 |
| --- | --- | --- |
| 便携版 | [MuseBox-v1.2.0-portable.zip](https://github.com/Xxuebi/MuseBox/releases/download/v1.2.0/MuseBox-v1.2.0-portable.zip) | 完整解压后运行 `MuseBox.exe` |
| 安装版 | [MuseBox-1.2.0.exe](https://github.com/Xxuebi/MuseBox/releases/download/v1.2.0/MuseBox-1.2.0.exe) | 运行安装向导，可选择桌面快捷方式与 `.mubo` 文件关联 |

两种版本功能相同，均需要 **Windows 10/11（推荐 x64）** 和 **[.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)**。请安装 **Desktop Runtime**，仅安装普通 .NET Runtime 不够。安装版使用传统 EXE 安装方式，不需要导入 MSIX 测试证书。

安装包暂未进行代码签名，Windows 可能提示未知发布者。请仅从本仓库 Releases 下载，并核对发行页的文件校验值。

### 升级与数据

- 升级前先退出 MuseBox，包括托盘中的实例。建议将重要画板另存为 `.mubo` 备份。
- 便携版请解压到新文件夹；安装版可安装到原位置进行更新。
- 默认数据路径为 `%LocalAppData%\MuseBox`，也可在“保存和加载”中调整。便携版表示程序免安装，**不表示素材库自动随程序文件夹移动**。
- 使用“链接原文件”导入的图片仍依赖原路径；移动、删除原图或换电脑后，链接可能失效。需要独立携带画板时，保存时选择“复制进画板”。

## 你可以用它做什么

### 先收集，再整理

小窗按抽屉分类，接收截图、剪贴板图片和本地图片。每个画板都有独立的素材区开关，默认开启：从小窗收集的图片先放在左侧素材区，整理时再拖入画板。

素材区可收纳、调整大小、多选和批量移入，重启后仍保留；直接粘贴或拖入画板的图片则直接放到画板上。

### 在自由画板上组织内容

- 缩放、平移、旋转和调整图片，添加文字注释与绘制内容。
- 图片、文字和绘制可混合组合，支持嵌套分组与图层排序。
- 自动排列、四向对齐、等距排列及水平／垂直分布，让素材更容易比较。
- 使用自适应线／点网格和移动吸附；临时去色，专注明暗关系。
- 鼠标穿透、透明画板和智能置顶模式，方便与其他应用配合使用。

### 保留原图，也能继续编辑

支持图片裁剪、旋转、翻转、透明度与颜色调整，以及 GIF 播放、逐帧浏览和动图复制。

导入时可选择：

- **复制进画板**：将图像复制到本地素材库，不再依赖原文件。
- **链接原文件**：不复制原图，原图修改后刷新；缺失时保留元素和路径提示。

导入方式可以按抽屉记忆，也可在画板设置中重新开启询问。

### 保存、分享和导出

- `.mubo` 保存画板内容、素材区、组合、图层和视图状态，并兼容旧版场景。
- 自动保存默认开启，定时更新已保存过的画板；未绑定场景文件的临时画板不会自动新建文件。
- 抽屉菜单支持保存、另存为，以及在当前抽屉打开 `.mubo`；替换未保存内容前会询问保存、取消或覆盖。
- 将 `.mubo` 拖到小窗可独立打开，不替换当前抽屉。
- 批量导出可保留原格式，或转换为 PNG / JPG / BMP，支持命名模板；也可将画板合成 PNG。
- “导出所有图像”的逐张导出包含素材区；所选导出、合成 PNG、排列与适应全部仍针对画板内容。

## 快速上手

1. 打开 MuseBox，选择或新建抽屉，收集截图或导入图片。
2. 点击“打开画板”，把左侧素材拖到画板，按需要排列、标注和分组。
3. 按 `Ctrl+S` 保存为 `.mubo`；外部链接需要随文件携带时，选择复制进画板。
4. 需要输出图片时，使用抽屉菜单“导出所有图像”，或画板右键菜单“保存 → 导出”。

| 常用操作 | 默认快捷键 |
| --- | --- |
| 复制 / 粘贴 | `Ctrl+C` / `Ctrl+V` |
| 撤回 / 重做 | `Ctrl+Z` / `Ctrl+Y` |
| 保存 / 另存为 | `Ctrl+S` / `Ctrl+Shift+S` |
| 自动排列 | `Ctrl+Alt+G` |
| 适应全部 | `Ctrl+0` |
| 组合 / 解散组合 | `Ctrl+G` / `Ctrl+Shift+G` |
| 退出画板模式 | `Ctrl+Shift+F12` |

快捷键可在设置中修改；系统级“退出画板模式”快捷键不受画板快捷键总开关影响。

## 从源码构建

需要 Windows、[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)；解决方案包含 WPF 应用、测试与 .NET Framework 缩略图组件。

```powershell
git clone https://github.com/Xxuebi/MuseBox.git
cd MuseBox
dotnet restore .\MuseBox.sln
dotnet build .\MuseBox.sln -c Release
dotnet run --project .\MuseBox.Tests\MuseBox.Tests.csproj -c Release
dotnet publish .\MuseBox.csproj -c Release -o .\publish\v1.2.0 --no-self-contained
```

安装版使用 [Inno Setup](https://jrsoftware.org/isinfo.php) 编译 `Installer/MuseBox.iss`，将上述发布目录中的程序打包。

源码主要分为 `Views`（窗口与界面）、`Controls`（自定义控件）、`Models`（数据模型）、`Services`（导入、资料库与场景服务），测试位于 `MuseBox.Tests`。

## 反馈与许可证

遇到问题或有功能建议，请提交 [Issue](https://github.com/Xxuebi/MuseBox/issues)，尽量附上版本、复现步骤和不含隐私的截图。详细版本变化见 [CHANGELOG.md](CHANGELOG.md)。

MuseBox 使用 [MIT License](LICENSE) 开源。

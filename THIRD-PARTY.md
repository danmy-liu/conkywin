# 第三方组件与素材

ConkyWin 本体的许可见 [LICENSE](LICENSE) 与 [NOTICE](NOTICE)。本文列出随发行包分发或运行时依赖的第三方组件，**它们各自保留原许可，不被本项目许可证替代**。分发 ConkyWin 时请一并带上本文件。

## 随发行包分发的组件

| 组件 | 版本 / 用途 | 原许可 | 版权声明 |
| --- | --- | --- | --- |
| [MoonSharp](https://github.com/moonsharp-devs/moonsharp) | 2.0.0，纯托管 Lua 解释器，被打包进 `ConkyWin.exe` | BSD 3-Clause | Copyright (c) 2014-2016, Marco Mastropaolo. All rights reserved.（完整条款见下方） |
| [.NET 8 / WPF 运行时](https://github.com/dotnet/runtime) | 自包含单文件发布，运行时已打包进 `ConkyWin.exe` | MIT（及各子组件的第三方声明） | 见 dotnet/runtime 仓库的 `LICENSE.TXT` / `ThirdPartyNotices.txt` |
| [Inno Setup 6](https://jrsoftware.org/isinfo.php) | 仅用于生成 `setup.exe` 安装向导；`installer/ChineseSimplified.isl` 取自 [jrsoftware/issrc](https://github.com/jrsoftware/issrc) | 按上游 Inno Setup 许可 | 见上游仓库；中文语言文件仅为向导译文，不参与程序本体 |

### MoonSharp — BSD 3-Clause 原文

```
Copyright (c) 2014-2016, Marco Mastropaolo
All rights reserved.

Parts of the string library are based on the KopiLua project
(https://github.com/NLua/KopiLua)
Copyright (c) 2012 LoDC

Visual Studio Code debugger code is based on code from Microsoft
vscode-mono-debug project (https://github.com/Microsoft/vscode-mono-debug).
Copyright (c) Microsoft Corporation - released under MIT license.

Remote Debugger icons are from the Eclipse project (https://www.eclipse.org/).
Copyright of The Eclipse Foundation

The MoonSharp icon is (c) Isaac, 2014-2015

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of the organization nor the names of its
  contributors may be used to endorse or promote products derived from
  this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

> 注：上游原文第三条中的占位符为 `{organization}`，此处按 BSD 3-Clause 通行写法记为 "the organization"，条款含义不变。

## 素材：来源与授权待确认

| 素材 | 用在哪 | 状态 |
| --- | --- | --- |
| 49 个白色单色天气 PNG（weather.com 0–47 代码编号） | `assets/icons/` | **来源与授权待确认**。由项目作者提供，未随附原始许可文件；在核实前，不对其作任何再授权声明，使用者需自行确认权利状况 |
| Harmattan conky 主题的 SVG / 版式参考 | `script.harmattan.lua` 的布局与配色 | **来源与授权待确认**。依据作者提供的主题素材复刻版式，未随附原始许可文件；在核实前，不对其作任何再授权声明 |

> 提醒：上述两项若实际来自第三方主题包（常见 GPL 或 CC BY-SA 等），其原许可可能不允许整体改用 PolyForm Noncommercial，也可能需要额外署名。核实来源后请更新本表；在此之前请勿将这些素材单独用于商业用途。

## 运行时调用的在线服务（不随包分发代码）

| 服务 | 用途 | 备注 |
| --- | --- | --- |
| [Open-Meteo](https://open-meteo.com) | 天气、三天预报、城市地理编码 | 免费版限非商业使用，无 API key |
| [ipwho.is](https://ipwho.is) / [freeipapi.com](https://freeipapi.com) | IP 自动定位（前两顺位，HTTPS） | 免费服务，各自服务条款以官网为准 |
| [ip-api.com](http://ip-api.com) | IP 自动定位（第三兜底源） | 免费端点仅支持 HTTP、明确不允许商业使用；HTTPS 需付费 Pro |

## 灵感来源

[Conky](https://github.com/brndnmtthws/conky) —— Linux 桌面监控挂件，本项目命名与形态的灵感来源，未引用其代码。

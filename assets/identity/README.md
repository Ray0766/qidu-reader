# TXT Reader 图标

0.1.5 的原创矢量标识：打开的书、抬起的右页、一条旧金色书签。延续书架的实体书语汇，不嵌入小字，保证标题栏和任务栏尺寸的辨认。未使用外部图库、字体或 AI 位图。

配色：墨绿 `#304A40`、纸白 `#F6F1DF`、旧金 `#B79C68`、背页 `#DAD8BF`；应用文字沿用 `#26352E`。书架空状态与列表互斥显示，文字居中，导入是主操作；有书时仍保留实体层板。标题使用宋体，说明和按钮使用微软雅黑 UI。

`app-icon.svg` 为唯一可编辑源；ICO 包含 16、20、24、32、40、48、64、128、256 像素，PNG 有 512 与 1024 像素。任务栏／窗口／应用 EXE／安装器统一使用 ICO；空书架使用 PNG。默认不生成渐变、光晕或小尺寸细线。

重新生成（Windows + .NET 10 SDK）：

```powershell
dotnet run --project tools/IconBuilder -- assets/identity/app-icon.svg src/TxtReader.App/Assets
```

生成器仅使用 WPF，将上述 SVG 的基本形状直接栅格化，无额外图形依赖。发布包不包含生成器。

## Microsoft Store 图标

使用同一 SVG 生成 MSIX 图标和 300 像素的商店展示图标：

```powershell
dotnet run --project tools/IconBuilder -- assets/identity/app-icon.svg installer/msix/Assets --store
```

生成 31 个 PNG，覆盖 100–400% 缩放和任务栏目标尺寸。原有应用 ICO/PNG 保持不变。MSIX 打包脚本调用 MakePri 建立资源索引，使 Windows 能按缩放选择图标。商店文案和截图位于 `installer/msix/store-materials`。

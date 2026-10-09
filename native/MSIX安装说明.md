# MSIX 安装说明

MSIX、安装 EXE、绿色 EXE 是同一个播放器的三种分发方式。安装 EXE 直接运行安装；绿色 EXE 双击即用；MSIX 交由 Windows 安装与卸载管理。

当前 MSIX 使用 `CN=NekoYume517` 自签名证书签名。首次在其它电脑安装前，需要明确信任随包提供的 `AutoMusicPlayerLite-MSIX.cer` 公钥证书。这不是微软商店签名，也不是系统预先信任的商业证书。只需要 CER，不要导入或分发私钥。

1. 双击 CER → 安装证书 → 本地计算机。
2. 选择“将所有的证书都放入下列存储”，浏览并选择“受信任人”（Trusted People），完成导入；Windows 会请求管理员授权。
3. 双击对应 MSIX，点击安装。之后从开始菜单打开播放器。

也可以在证书与 MSIX 所在目录，以管理员身份打开 PowerShell，执行：

```powershell
Import-Certificate -FilePath '.\AutoMusicPlayerLite-MSIX.cer' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
```

然后用普通 PowerShell 安装对应的 MSIX：

```powershell
Add-AppxPackage -Path '.\AutoMusicPlayerLite-2.2.5-x64.msix'
```

自用版请选择文件名带 `Private` 的 MSIX。自用版与开源版相互独立；同一版本类型的三种分发方式共用本地曲库与设置。软件更新会选择相同分发方式：MSIX 更新 MSIX，安装 EXE 更新安装 EXE，绿色 EXE 替换当前绿色文件。

安装报 `0x800B0109` 或无法验证发布者时，确认导入的是这个版本配套的 CER，且放在“本地计算机 → 受信任人”。无管理员权限或不方便信任证书时，使用安装 EXE 或绿色 EXE。

支持 Windows 10 2004 及以上、Windows 11 x64。包内已包含所需运行库。

证书信任步骤依据 [微软 MSIX 签名说明](https://learn.microsoft.com/en-us/windows/msix/package/sign-msix-package-guide#testing-distribute-to-testers-with-a-self-signed-certificate)。

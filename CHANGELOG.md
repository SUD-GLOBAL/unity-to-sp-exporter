# 更新记录

## [1.0.7]

### 新增

- 支持通过 Unity Package Manager 安装插件，提供用于 Git 分发的 UPM 包目录和可选的 `.tgz` 安装包。

### 调整

- 插件版本采用三段式 `1.0.7`，对应原四段式版本 `1.0.0.7`。
- UPM 安装时，用户配置保存在 `Assets/SudExporterConfig/SudExporterSettings.asset`。
- unitypackage 保留原有安装结构及配置位置。

### 升级说明

- 首次从 Assets 中的旧插件切换到 UPM 时，先记录已有配置，删除旧插件，再安装 UPM 并重新填写配置。旧配置不会自动迁移。

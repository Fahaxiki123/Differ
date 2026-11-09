# Differ.UI

- 先读 [项目规范](../../docs/DEVELOPMENT_SPEC.md) 和 [组件目录](../../docs/ui/COMPONENTS.md)。
- 先找现有组件。外观差异优先用显式样式和 Dui Token。两套配色同键同类型。
- 仅依赖 WPF 基础库。业务模型、编辑器、安全保存和差异语义留在 App。
- 使用现有主题入口。变更后验证双主题、键盘和真实使用场景，按验收范围迁移。

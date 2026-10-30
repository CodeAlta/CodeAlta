// English source keys; translations in es, fr, de, ja, zh-CN order.
// The form of the Agent chip of a session started by an automation, a task or an issue.
export const sessionSettingsMessages = {
  "Use the defaults": ["Usar los valores predeterminados", "Utiliser les valeurs par défaut", "Standardwerte verwenden", "既定値を使用", "使用默认值"],
  "{name} (first enabled)": ["{name} (primero habilitado)", "{name} (premier activé)", "{name} (erster aktivierter)", "{name}（最初に有効なもの）", "{name}（第一个已启用）"],
  // The options of a session, under More options.
  "More options": ["Más opciones", "Plus d'options", "Weitere Optionen", "その他のオプション", "更多选项"],
  "{count} changed": ["{count} cambiada(s)", "{count} modifiée(s)", "{count} geändert", "{count} 件変更", "已更改 {count} 项"],
  "Sub-sessions": ["Subsesiones", "Sous-sessions", "Untersitzungen", "サブセッション", "子会话"],
  "The agent can start sessions of its own (sub-agents).": ["El agente puede iniciar sus propias sesiones (subagentes).", "L'agent peut démarrer ses propres sessions (sous-agents).",
    "Der Agent kann eigene Sitzungen starten (Subagenten).", "エージェントは自分のセッション（サブエージェント）を開始できます。", "代理可以启动自己的会话（子代理）。"],
  "The agent does all the work itself: it starts no session.": ["El agente hace todo el trabajo por sí mismo: no inicia ninguna sesión.", "L'agent fait tout le travail lui-même : il ne démarre aucune session.",
    "Der Agent erledigt die ganze Arbeit selbst: Er startet keine Sitzung.", "エージェントはすべての作業を自分で行い、セッションを開始しません。", "代理自己完成所有工作：不启动任何会话。"],
  "Context size": ["Tamaño del contexto", "Taille du contexte", "Kontextgröße", "コンテキストサイズ", "上下文大小"],
  "The conversation is compacted before it exceeds this size.": ["La conversación se compacta antes de superar este tamaño.",
    "La conversation est compactée avant de dépasser cette taille.", "Die Unterhaltung wird verdichtet, bevor sie diese Größe überschreitet.",
    "会話はこのサイズを超える前に圧縮されます。", "对话在超过此大小之前会被压缩。"],
  "Default size for this provider's sessions, when their model offers it.": ["Tamaño predeterminado de las sesiones de este proveedor, si su modelo lo ofrece.",
    "Taille par défaut des sessions de ce fournisseur, si leur modèle la propose.", "Standardgröße für die Sitzungen dieses Anbieters, wenn ihr Modell sie anbietet.",
    "このプロバイダーのセッションの既定サイズ（モデルが対応している場合）。", "此提供商会话的默认大小（模型支持时）。"],
  "The options were not changed ({status}).": ["Las opciones no se cambiaron ({status}).", "Les options n'ont pas été modifiées ({status}).",
    "Die Optionen wurden nicht geändert ({status}).", "オプションは変更されませんでした（{status}）。", "选项未更改（{status}）。"],
} satisfies Record<string, readonly [string, string, string, string, string]>;

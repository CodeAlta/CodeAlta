// English source keys; translations in es, fr, de, ja, zh-CN order.
export const modelTierMessages = {
  // Settings > Models: the tiers of models and the fallback.
  "Model tiers": ["Niveles de modelos", "Niveaux de modèles", "Modellstufen", "モデルの階層", "模型层级"],
  "The models you prefer for complex, standard and quick work, in order.": ["Los modelos que prefieres para el trabajo complejo, estándar y rápido, en orden.", "Les modèles que vous préférez pour le travail complexe, standard et rapide, dans l’ordre.", "Die Modelle, die Sie für komplexe, normale und schnelle Arbeit bevorzugen, in Reihenfolge.", "複雑・標準・簡単な作業に使いたいモデルを優先順に。", "按顺序列出你在复杂、标准和快速工作中偏好的模型。"],
  "Complex": ["Complejo", "Complexe", "Komplex", "複雑", "复杂"],
  "Standard": ["Estándar", "Standard", "Standard", "標準", "标准"],
  "Quick": ["Rápido", "Rapide", "Schnell", "簡単", "快速"],
  "Gives {model}": ["Da {model}", "Donne {model}", "Ergibt {model}", "{model} を使用", "使用 {model}"],
  "No model ready": ["Ningún modelo listo", "Aucun modèle prêt", "Kein Modell bereit", "使用できるモデルなし", "没有可用模型"],
  "No model yet.": ["Aún no hay modelo.", "Pas encore de modèle.", "Noch kein Modell.", "モデルはまだありません。", "尚无模型。"],
  "Add a model": ["Añadir un modelo", "Ajouter un modèle", "Modell hinzufügen", "モデルを追加", "添加模型"],
  "Add a model to {tier}": ["Añadir un modelo a {tier}", "Ajouter un modèle à {tier}", "Modell zu {tier} hinzufügen", "{tier} にモデルを追加", "向 {tier} 添加模型"],
  "Add an effort": ["Añadir un esfuerzo", "Ajouter un effort", "Aufwand hinzufügen", "推論レベルを追加", "添加推理强度"],
  "Move {name}": ["Mover {name}", "Déplacer {name}", "{name} verschieben", "{name} を移動", "移动 {name}"],
  "Remove {name} from {tier}": ["Quitar {name} de {tier}", "Retirer {name} de {tier}", "{name} aus {tier} entfernen", "{tier} から {name} を削除", "从 {tier} 移除 {name}"],
  "Remove {effort}": ["Quitar {effort}", "Retirer {effort}", "{effort} entfernen", "{effort} を削除", "移除 {effort}"],
  "Not a configured provider": ["No es un proveedor configurado", "Pas un fournisseur configuré", "Kein konfigurierter Anbieter", "設定済みのプロバイダーではありません", "不是已配置的提供商"],
  "The tier has this model already.": ["El nivel ya tiene este modelo.", "Le niveau a déjà ce modèle.", "Die Stufe enthält dieses Modell bereits.", "この階層にはすでにこのモデルがあります。", "该层级已包含此模型。"],
  "Fall back to the next model of the tier when a provider fails": ["Pasar al siguiente modelo del nivel cuando falla un proveedor", "Passer au modèle suivant du niveau quand un fournisseur échoue", "Bei Ausfall eines Anbieters zum nächsten Modell der Stufe wechseln", "プロバイダーが失敗したら階層の次のモデルに切り替える", "提供商失败时切换到该层级的下一个模型"],
  "Quota reached": ["Cuota alcanzada", "Quota atteint", "Kontingent erreicht", "クォータ到達", "已达配额"],
  "Provider unavailable": ["Proveedor no disponible", "Fournisseur indisponible", "Anbieter nicht verfügbar", "プロバイダー利用不可", "提供商不可用"],
  "The model tiers were not saved ({status}).": ["Los niveles de modelos no se guardaron ({status}).", "Les niveaux de modèles n’ont pas été enregistrés ({status}).", "Die Modellstufen wurden nicht gespeichert ({status}).", "モデルの階層は保存されませんでした ({status})。", "模型层级未保存（{status}）。"],
  // The composer: a tier at the top of the model list.
  "Tiers": ["Niveles", "Niveaux", "Stufen", "階層", "层级"],
  "No model of the {tier} tier is ready.": ["Ningún modelo del nivel {tier} está listo.", "Aucun modèle du niveau {tier} n’est prêt.", "Kein Modell der Stufe {tier} ist bereit.", "{tier} 階層に使用できるモデルがありません。", "{tier} 层级没有可用模型。"],
  "The tier was not changed ({status}).": ["El nivel no se cambió ({status}).", "Le niveau n’a pas été changé ({status}).", "Die Stufe wurde nicht geändert ({status}).", "階層は変更されませんでした ({status})。", "层级未更改（{status}）。"],
  // The page of a space: its new sessions.
  "New sessions": ["Nuevas sesiones", "Nouvelles sessions", "Neue Sitzungen", "新しいセッション", "新会话"],
  "Use global": ["Usar los globales", "Utiliser les globaux", "Globale verwenden", "グローバルを使用", "使用全局设置"],
  "{tier} tier": ["Nivel {tier}", "Niveau {tier}", "Stufe {tier}", "{tier} 階層", "{tier} 层级"],
} as const;

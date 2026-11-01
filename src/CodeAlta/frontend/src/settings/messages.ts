// English source keys; translations in es, fr, de, ja, zh-CN order.
// The settings as one catalog: the search of Settings, its results, the settings in the search of the window, and the
// toast of a change made by an agent.
export const settingsSearchMessages = {
  "Search settings": ["Buscar ajustes", "Rechercher dans les paramètres", "Einstellungen durchsuchen", "設定を検索", "搜索设置"],
  "No setting matches “{query}”.": ["Ningún ajuste coincide con «{query}».", "Aucun paramètre ne correspond à « {query} ».", "Keine Einstellung passt zu „{query}“.",
    "「{query}」に一致する設定はありません。", "没有与“{query}”匹配的设置。"],
  "{count} results for “{query}”": ["{count} resultados para «{query}»", "{count} résultats pour « {query} »", "{count} Ergebnisse für „{query}“", "「{query}」の結果 {count} 件",
    "“{query}”的 {count} 个结果"],
  "Open page": ["Abrir la página", "Ouvrir la page", "Seite öffnen", "ページを開く", "打开页面"],
  "Change it on its page": ["Cámbialo en su página", "Modifiez-le sur sa page", "Auf seiner Seite ändern", "そのページで変更します", "在其页面上更改"],
  "Not available here": ["No disponible aquí", "Non disponible ici", "Hier nicht verfügbar", "ここでは利用できません", "此处不可用"],
  "Applies with the next prompt": ["Se aplica con el próximo prompt", "S’applique au prochain prompt", "Gilt ab dem nächsten Prompt", "次のプロンプトから適用されます", "从下一个提示起生效"],
  "Back to its default": ["Volver al valor predeterminado", "Revenir à sa valeur par défaut", "Auf den Standardwert zurücksetzen", "既定値に戻す", "恢复默认值"],
  "The value was not accepted.": ["El valor no fue aceptado.", "La valeur n’a pas été acceptée.", "Der Wert wurde nicht angenommen.", "値は受け付けられませんでした。", "该值未被接受。"],
  "Setting": ["Ajuste", "Paramètre", "Einstellung", "設定", "设置"],
  "Settings page": ["Página de ajustes", "Page des paramètres", "Einstellungsseite", "設定ページ", "设置页面"],
  "{who} changed {setting} to {value}": ["{who} cambió {setting} a {value}", "{who} a changé {setting} en {value}", "{who} hat {setting} auf {value} geändert",
    "{who} が {setting} を {value} に変更しました", "{who} 将 {setting} 更改为 {value}"],
  "{setting} was changed to {value}": ["{setting} se cambió a {value}", "{setting} a été changé en {value}", "{setting} wurde auf {value} geändert",
    "{setting} が {value} に変更されました", "{setting} 已更改为 {value}"],
  "Undo": ["Deshacer", "Annuler", "Rückgängig", "元に戻す", "撤销"],
  "The change could not be undone.": ["No se pudo deshacer el cambio.", "La modification n’a pas pu être annulée.", "Die Änderung konnte nicht rückgängig gemacht werden.",
    "変更を元に戻せませんでした。", "无法撤销此更改。"],
} as const;

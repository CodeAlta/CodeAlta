// English source keys; translations in es, fr, de, ja, zh-CN order.
export const sessionLinkMessages = {
  // The tooltip of the chip of a link to a session in a text: its title, its state, its project and when it was last active.
  "Session {title}: {state}, in {scope}, last active {time}": ["Sesión {title}: {state}, en {scope}, última actividad {time}",
    "Session {title} : {state}, dans {scope}, dernière activité {time}", "Sitzung {title}: {state}, in {scope}, zuletzt aktiv {time}",
    "セッション {title}: {state}、{scope}、最終アクティビティ {time}", "会话 {title}：{state}，位于 {scope}，最后活动 {time}"],
  "Session {title}: {state}, last active {time}": ["Sesión {title}: {state}, última actividad {time}", "Session {title} : {state}, dernière activité {time}",
    "Sitzung {title}: {state}, zuletzt aktiv {time}", "セッション {title}: {state}、最終アクティビティ {time}", "会话 {title}：{state}，最后活动 {time}"],
  // A link names a session this window does not know (deleted, or of another computer).
  "Unknown session {id}": ["Sesión desconocida {id}", "Session inconnue {id}", "Unbekannte Sitzung {id}", "不明なセッション {id}", "未知会话 {id}"],
  "Copy session id": ["Copiar el id de la sesión", "Copier l'id de la session", "Sitzungs-ID kopieren", "セッション ID をコピー", "复制会话 ID"],
  "The session of this link could not be found.": ["No se encontró la sesión de este enlace.", "La session de ce lien est introuvable.",
    "Die Sitzung dieses Links wurde nicht gefunden.", "このリンクのセッションが見つかりませんでした。", "找不到此链接的会话。"],
} as const;

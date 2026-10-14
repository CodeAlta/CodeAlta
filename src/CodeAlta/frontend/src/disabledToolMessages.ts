// English source keys; translations in es, fr, de, ja, zh-CN order.
// The tools a session does without: the Permissions page, the page of a space and the editor of an agent prompt.
export const disabledToolMessages = {
  "Tools turned off": ["Herramientas desactivadas", "Outils désactivés", "Ausgeschaltete Werkzeuge", "オフにしたツール", "已关闭的工具"],
  "Turn off a tool": ["Desactivar una herramienta", "Désactiver un outil", "Ein Werkzeug ausschalten", "ツールをオフにする", "关闭一个工具"],
  "Another tool": ["Otra herramienta", "Un autre outil", "Ein anderes Werkzeug", "ほかのツール", "其他工具"],
  "Turn {name} on again": ["Volver a activar {name}", "Réactiver {name}", "{name} wieder einschalten", "{name} をオンに戻す", "重新打开 {name}"],
  "Turned off for every session": ["Desactivada para todas las sesiones", "Désactivé pour toutes les sessions", "Für jede Sitzung ausgeschaltet", "すべてのセッションでオフ", "对所有会话关闭"],
  "A session of this agent does without them.": ["Una sesión de este agente no las tiene.", "Une session de cet agent s’en passe.", "Eine Sitzung dieses Agenten verzichtet darauf.", "このエージェントのセッションはこれらを使いません。", "此代理的会话不使用它们。"],
  "The sessions of this space do without them.": ["Las sesiones de este espacio no las tienen.", "Les sessions de cet espace s’en passent.", "Die Sitzungen dieses Bereichs verzichten darauf.", "このスペースのセッションはこれらを使いません。", "此空间的会话不使用它们。"],
  "Every session does without them, whatever its space and its agent.": ["Ninguna sesión las tiene, sea cual sea su espacio y su agente.", "Aucune session ne les a, quels que soient son espace et son agent.", "Keine Sitzung hat sie, gleich in welchem Bereich und mit welchem Agenten.", "スペースやエージェントにかかわらず、どのセッションもこれらを使いません。", "无论空间和代理如何，所有会话都不使用它们。"],
} as const;

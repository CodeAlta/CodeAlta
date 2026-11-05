// The words of the search in the timeline of a session, in the five languages after English (es, fr, de, ja, zh-CN),
// merged into the dictionary of `localization.ts`. English is the key.
type Row = readonly [string, string, string, string, string];

export const findInSessionMessages = {
  "Find in Session": ["Buscar en la sesión", "Rechercher dans la session", "In der Sitzung suchen", "セッション内を検索", "在会话中查找"],
  "Find a text in the timeline of this session, with every match marked.": ["Busca un texto en la cronología de esta sesión y marca todas las coincidencias.",
    "Recherche un texte dans le fil de cette session et marque chaque occurrence.", "Sucht einen Text im Verlauf dieser Sitzung und markiert jeden Treffer.",
    "このセッションのタイムラインでテキストを検索し、一致箇所をすべてマークします。", "在此会话的时间线中查找文本，并标记所有匹配项。"],
  "Find in this session": ["Buscar en esta sesión", "Rechercher dans cette session", "In dieser Sitzung suchen", "このセッション内を検索", "在此会话中查找"],
  "Only the loaded messages are searched. Load all messages to search them too.": ["Solo se buscan los mensajes cargados. Carga todos los mensajes para buscar también en ellos.",
    "Seuls les messages chargés sont recherchés. Chargez tous les messages pour les rechercher aussi.", "Nur geladene Nachrichten werden durchsucht. Lade alle Nachrichten, um auch diese zu durchsuchen.",
    "読み込み済みのメッセージのみ検索されます。すべて検索するには、すべてのメッセージを読み込んでください。", "仅搜索已加载的消息。加载全部消息后可一并搜索。"],
} as const satisfies Readonly<Record<string, Row>>;

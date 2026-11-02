// The words of the start prompts on the page of prompts of Settings, in the five languages after English (es, fr,
// de, ja, zh-CN), merged into the dictionary of `localization.ts`. English is the key. A placeholder in braces that
// no value replaces is shown as it is: `{reference}` names the placeholder of the prompt.
type Row = readonly [string, string, string, string, string];

export const startPromptMessages = {
  "Start prompts": ["Prompts de inicio", "Prompts de démarrage", "Start-Prompts", "開始プロンプト", "启动提示词"],
  "What a session is told first when you start the review of a pull request from the Issues tab.": [
    "Lo primero que se dice a una sesión cuando inicias la revisión de una pull request desde la pestaña Issues.",
    "Ce qui est dit d’abord à une session quand vous lancez la revue d’une pull request depuis l’onglet Issues.",
    "Was einer Sitzung zuerst gesagt wird, wenn Sie das Review eines Pull Requests im Issues-Tab starten.",
    "Issues タブからプルリクエストのレビューを開始したときに、セッションに最初に伝える内容。",
    "从 Issues 标签页开始审查拉取请求时，最先告诉会话的内容。"],
  "{reference}, {location} and {service} are replaced by the number of the pull request, its repository and its service. Its title, its link and its description follow this text.": [
    "{reference}, {location} y {service} se sustituyen por el número de la pull request, su repositorio y su servicio. Su título, su enlace y su descripción siguen a este texto.",
    "{reference}, {location} et {service} sont remplacés par le numéro de la pull request, son dépôt et son service. Son titre, son lien et sa description suivent ce texte.",
    "{reference}, {location} und {service} werden durch die Nummer des Pull Requests, sein Repository und seinen Dienst ersetzt. Titel, Link und Beschreibung folgen diesem Text.",
    "{reference}、{location}、{service} は、プルリクエストの番号、リポジトリ、サービスに置き換えられます。タイトル、リンク、説明はこのテキストの後に続きます。",
    "{reference}、{location} 和 {service} 会被替换为拉取请求的编号、其仓库和其服务。其标题、链接和描述跟在此文本之后。"],
  "This is the text CodeAlta ships. To change it, make a copy of your own: it takes its place.": [
    "Este es el texto que incluye CodeAlta. Para cambiarlo, haz una copia propia: ocupará su lugar.",
    "Voici le texte fourni avec CodeAlta. Pour le modifier, faites-en une copie : elle prend sa place.",
    "Das ist der Text, der mit CodeAlta ausgeliefert wird. Um ihn zu ändern, erstellen Sie eine eigene Kopie: Sie tritt an seine Stelle.",
    "これは CodeAlta に付属するテキストです。変更するには自分用のコピーを作成してください。コピーがこのテキストの代わりに使われます。",
    "这是 CodeAlta 自带的文本。要更改它，请制作自己的副本：副本将取代它。"],
} satisfies Record<string, Row>;
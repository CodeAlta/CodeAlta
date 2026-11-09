// The limit of requests at once of a provider, in its form: English source keys, with the five languages after English
// (es, fr, de, ja, zh-CN), merged into the dictionary of `localization.ts`. English is the key.
export const providerRequestMessages = {
  "Requests at once": ["Solicitudes a la vez", "Requêtes simultanées", "Gleichzeitige Anfragen", "同時リクエスト数", "同时请求数"],
  "The most requests this provider gets at once; the sessions beyond it wait their turn.": [
    "El máximo de solicitudes que recibe este proveedor a la vez; las sesiones de más esperan su turno.",
    "Le nombre maximal de requêtes que reçoit ce fournisseur à la fois ; les sessions au-delà attendent leur tour.",
    "Die meisten Anfragen, die dieser Anbieter gleichzeitig erhält; weitere Sitzungen warten, bis sie an der Reihe sind.",
    "このプロバイダーが同時に受けるリクエストの上限です。それを超えるセッションは順番を待ちます。",
    "此提供商同时接收的最多请求数；超出的会话按顺序等待。",
  ],
  // The notice of a session whose request waits, in its timeline.
  "Waiting for a free request": ["Esperando una solicitud libre", "En attente d’une requête libre", "Wartet auf eine freie Anfrage", "空きリクエストを待機中", "正在等待空闲请求"],
  "Waiting for a free slot": ["Esperando un hueco libre", "En attente d’un emplacement libre", "Wartet auf einen freien Slot", "空きスロットを待機中", "正在等待空闲槽位"],
  "Requests at once is a whole number greater than zero.": [
    "Solicitudes a la vez es un número entero mayor que cero.",
    "Requêtes simultanées est un nombre entier supérieur à zéro.",
    "Gleichzeitige Anfragen ist eine ganze Zahl größer als null.",
    "同時リクエスト数は 0 より大きい整数です。",
    "同时请求数为大于零的整数。",
  ],
} as const;

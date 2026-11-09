// English source keys; translations in es, fr, de, ja, zh-CN order.
export const seatMessages = {
  // The limits of a machine, on its page.
  "Sessions at work at once": ["Sesiones trabajando a la vez", "Sessions au travail à la fois", "Gleichzeitig arbeitende Sitzungen", "同時に作業するセッション", "同时工作的会话"],
  "No limit": ["Sin límite", "Aucune limite", "Keine Grenze", "制限なし", "无限制"],
  "{name} runs at most this many sessions at once, a session and its sub-sessions counting once. The work of other machines beyond it waits for its turn; the work started on {name} itself never waits.": [
    "{name} ejecuta como máximo este número de sesiones a la vez; una sesión y sus subsesiones cuentan una vez. El trabajo de otras máquinas que lo supere espera su turno; el trabajo iniciado en {name} nunca espera.",
    "{name} exécute au plus ce nombre de sessions à la fois, une session et ses sous-sessions comptant pour une. Au-delà, le travail des autres machines attend son tour ; le travail lancé sur {name} même n’attend jamais.",
    "{name} führt höchstens so viele Sitzungen gleichzeitig aus; eine Sitzung und ihre Untersitzungen zählen einmal. Darüber hinaus wartet die Arbeit anderer Maschinen, bis sie an der Reihe ist; auf {name} selbst gestartete Arbeit wartet nie.",
    "{name} が同時に実行するセッションはこの数までです（セッションとそのサブセッションは 1 つと数えます）。それを超える他のマシンからの作業は順番を待ちます。{name} 自体で開始した作業は待ちません。",
    "{name} 最多同时运行这么多会话，一个会话及其子会话计为一个。超出部分来自其他机器的工作将排队等候；在 {name} 本机启动的工作从不等待。"],
  "A number from 1 to {max}, or empty for no limit.": ["Un número de 1 a {max}, o vacío para no tener límite.", "Un nombre de 1 à {max}, ou vide pour aucune limite.", "Eine Zahl von 1 bis {max}, oder leer für keine Grenze.", "1 から {max} までの数値。空欄で制限なし。", "1 到 {max} 之间的数字，留空表示无限制。"],
  "{working} of {limit} at work": ["{working} de {limit} trabajando", "{working} sur {limit} au travail", "{working} von {limit} bei der Arbeit", "{limit} 中 {working} が作業中", "{working}/{limit} 正在工作"],
  "{working} at work": ["{working} trabajando", "{working} au travail", "{working} bei der Arbeit", "{working} が作業中", "{working} 正在工作"],
  "Nothing waits for its turn.": ["Nada espera su turno.", "Rien n’attend son tour.", "Nichts wartet, bis es an der Reihe ist.", "順番を待つものはありません。", "没有排队等候的工作。"],
  "Start now": ["Iniciar ahora", "Démarrer maintenant", "Jetzt starten", "今すぐ開始", "立即开始"],
  "Take out of the queue": ["Quitar de la cola", "Retirer de la file", "Aus der Warteschlange nehmen", "キューから外す", "移出队列"],
  "An automation": ["Una automatización", "Une automatisation", "Eine Automatisierung", "自動化", "一个自动化"],
  "A prompt": ["Un prompt", "Un prompt", "Ein Prompt", "プロンプト", "一个提示"],
  // A session whose run waits for a seat of its machine.
  "Queued on {machine} (next)": ["En cola en {machine} (la siguiente)", "En attente sur {machine} (la suivante)", "Wartet auf {machine} (als Nächstes)", "{machine} で待機中（次）", "在 {machine} 上排队（下一个）"],
  "Queued on {machine} ({count} before it)": ["En cola en {machine} ({count} antes)", "En attente sur {machine} ({count} avant)", "Wartet auf {machine} ({count} davor)", "{machine} で待機中（前に {count} 件）", "在 {machine} 上排队（前面还有 {count} 个）"],
  "{machine} runs as many sessions as its owner lets it: this session starts when its turn comes.": [
    "{machine} ejecuta tantas sesiones como su propietario le permite: esta sesión empieza cuando llegue su turno.",
    "{machine} exécute autant de sessions que son propriétaire le permet : cette session démarre quand vient son tour.",
    "{machine} führt so viele Sitzungen aus, wie sein Besitzer erlaubt: Diese Sitzung beginnt, wenn sie an der Reihe ist.",
    "{machine} は所有者が許可する数のセッションを実行中です。このセッションは順番が来たら開始します。",
    "{machine} 正在运行其所有者允许的最多会话：轮到时此会话将开始。"],
  "The session started on {machine}: its prompt waits for its turn there.": [
    "La sesión se inició en {machine}: su prompt espera allí su turno.",
    "La session a démarré sur {machine} : son prompt y attend son tour.",
    "Die Sitzung wurde auf {machine} gestartet: Ihr Prompt wartet dort, bis er an der Reihe ist.",
    "セッションは {machine} で開始しました。プロンプトはそこで順番を待っています。",
    "会话已在 {machine} 上启动：其提示在那里排队等候。"],
  // A run of an automation that waits for a seat.
  "Waits for its turn": ["Espera su turno", "Attend son tour", "Wartet, bis es an der Reihe ist", "順番待ち", "等候轮到"],
} as const;

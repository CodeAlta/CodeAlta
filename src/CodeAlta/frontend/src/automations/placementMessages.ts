// English source keys; translations in es, fr, de, ja, zh-CN order.
export const automationPlacementMessages = {
  "Runs on": ["Se ejecuta en", "S’exécute sur", "Läuft auf", "実行場所", "运行于"],
  "{machine} (offline)": ["{machine} (sin conexión)", "{machine} (hors ligne)", "{machine} (offline)", "{machine}（オフライン）", "{machine}（离线）"],
  "Unavailable machine": ["Máquina no disponible", "Machine indisponible", "Nicht verfügbare Maschine", "利用できないマシン", "不可用的机器"],
  "Waits for Continue anyway": ["Espera a continuar de todos modos", "Attend Continuer quand même", "Wartet auf Trotzdem fortsetzen", "それでも続行を待っています", "等待仍然继续"],
  "{name} waits for Continue anyway on {machine}: a budget of the machine is reached.": [
    "{name} espera a continuar de todos modos en {machine}: se alcanzó un presupuesto de la máquina.",
    "{name} attend Continuer quand même sur {machine} : un budget de la machine est atteint.",
    "{name} wartet auf {machine} auf Trotzdem fortsetzen: Ein Budget der Maschine ist erreicht.",
    "{name} は {machine} で「それでも続行」を待っています。マシンの予算が上限に達しました。",
    "{name} 在 {machine} 上等待仍然继续：该机器的预算已达上限。"],
  "On {machine}": ["En {machine}", "Sur {machine}", "Auf {machine}", "{machine} 上", "在 {machine} 上"],
  "Copied to {machine}": ["Copiada en {machine}", "Copiée sur {machine}", "Auf {machine} kopiert", "{machine} にコピー済み", "已复制到 {machine}"],
  "The machine of this run does not list its session now.": [
    "La máquina de esta ejecución no muestra su sesión ahora.",
    "La machine de cette exécution ne liste pas sa session pour l’instant.",
    "Die Maschine dieses Laufs listet seine Sitzung gerade nicht.",
    "この実行のマシンは現在そのセッションを一覧にしていません。",
    "此运行所在的机器当前未列出其会话。"],
  "Waits for the machine it left": [
    "Espera a la máquina que dejó",
    "Attend la machine qu’elle a quittée",
    "Wartet auf die Maschine, die sie verlassen hat",
    "移動元のマシンを待っています",
    "正在等待原来的机器"],
  "Start anyway…": ["Iniciar de todos modos…", "Démarrer quand même…", "Trotzdem starten…", "このまま開始…", "仍然启动…"],
  "Start anyway": ["Iniciar de todos modos", "Démarrer quand même", "Trotzdem starten", "このまま開始", "仍然启动"],
  "Start it on {machine} now?": ["¿Iniciarla ahora en {machine}?", "La démarrer maintenant sur {machine} ?", "Jetzt auf {machine} starten?", "今すぐ {machine} で開始しますか？", "现在在 {machine} 上启动？"],
  "Start it on this computer now?": ["¿Iniciarla ahora en este ordenador?", "La démarrer maintenant sur cet ordinateur ?", "Jetzt auf diesem Computer starten?", "今すぐこのコンピューターで開始しますか？", "现在在本机上启动？"],
  "The machine it left may still run its copy until it answers: both machines may run it meanwhile.": [
    "La máquina que dejó puede seguir ejecutando su copia hasta que responda: mientras tanto, ambas máquinas pueden ejecutarla.",
    "La machine qu’elle a quittée peut encore exécuter sa copie jusqu’à ce qu’elle réponde : les deux machines peuvent l’exécuter d’ici là.",
    "Die Maschine, die sie verlassen hat, kann ihre Kopie noch ausführen, bis sie antwortet: Bis dahin können beide Maschinen sie ausführen.",
    "移動元のマシンは応答するまでコピーを実行し続ける可能性があります。その間、両方のマシンで実行されることがあります。",
    "原来的机器在响应之前可能仍会运行其副本：在此期间两台机器都可能运行它。"],
  "A command trigger runs on this computer only.": [
    "Un disparador de comando solo se ejecuta en este ordenador.",
    "Un déclencheur de commande ne s’exécute que sur cet ordinateur.",
    "Ein Befehlsauslöser läuft nur auf diesem Computer.",
    "コマンドトリガーはこのコンピューターでのみ実行されます。",
    "命令触发器只在本机运行。"],
} as const;

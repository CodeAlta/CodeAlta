// English source keys; translations in es, fr, de, ja, zh-CN order.
export const automationPlacementMessages = {
  "Runs on": ["Se ejecuta en", "S’exécute sur", "Läuft auf", "実行場所", "运行于"],
  "{machine} (offline)": ["{machine} (sin conexión)", "{machine} (hors ligne)", "{machine} (offline)", "{machine}（オフライン）", "{machine}（离线）"],
  "Unavailable machine": ["Máquina no disponible", "Machine indisponible", "Nicht verfügbare Maschine", "利用できないマシン", "不可用的机器"],
  "On {machine}": ["En {machine}", "Sur {machine}", "Auf {machine}", "{machine} 上", "在 {machine} 上"],
  "Copied to {machine}": ["Copiada en {machine}", "Copiée sur {machine}", "Auf {machine} kopiert", "{machine} にコピー済み", "已复制到 {machine}"],
  "The machine of this run does not list its session now.": [
    "La máquina de esta ejecución no muestra su sesión ahora.",
    "La machine de cette exécution ne liste pas sa session pour l’instant.",
    "Die Maschine dieses Laufs listet seine Sitzung gerade nicht.",
    "この実行のマシンは現在そのセッションを一覧にしていません。",
    "此运行所在的机器当前未列出其会话。"],
  "A command trigger runs on this computer only.": [
    "Un disparador de comando solo se ejecuta en este ordenador.",
    "Un déclencheur de commande ne s’exécute que sur cet ordinateur.",
    "Ein Befehlsauslöser läuft nur auf diesem Computer.",
    "コマンドトリガーはこのコンピューターでのみ実行されます。",
    "命令触发器只在本机运行。"],
} as const;

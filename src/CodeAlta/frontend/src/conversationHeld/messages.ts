// English source keys; translations in es, fr, de, ja, zh-CN order.
export const conversationHeldMessages = {
  // The card of a run whose Claude Code conversation another process holds.
  "Claude Code conversation open elsewhere": ["Conversación de Claude Code abierta en otro lugar", "Conversation Claude Code ouverte ailleurs", "Claude-Code-Unterhaltung woanders geöffnet", "別の場所で開いている Claude Code の会話", "在别处打开的 Claude Code 对话"],
  "This Claude Code conversation is open in {origin}": ["Esta conversación de Claude Code está abierta en {origin}", "Cette conversation Claude Code est ouverte dans {origin}", "Diese Claude-Code-Unterhaltung ist in {origin} geöffnet", "この Claude Code の会話は {origin} で開いています", "此 Claude Code 对话已在 {origin} 中打开"],
  "Process {pid}": ["Proceso {pid}", "Processus {pid}", "Prozess {pid}", "プロセス {pid}", "进程 {pid}"],
  "Wait until it is free": ["Esperar a que quede libre", "Attendre qu’elle soit libre", "Warten, bis sie frei ist", "空くまで待つ", "等待其释放"],
  "Continue as a copy": ["Continuar en una copia", "Continuer dans une copie", "Als Kopie fortsetzen", "コピーとして続ける", "作为副本继续"],
  "Open here anyway": ["Abrir aquí de todos modos", "Ouvrir ici quand même", "Trotzdem hier öffnen", "それでもここで開く", "仍在此处打开"],
  "Both would write the same conversation": ["Ambos escribirían la misma conversación", "Les deux écriraient la même conversation", "Beide würden in dieselbe Unterhaltung schreiben", "両方が同じ会話に書き込みます", "双方会写入同一个对话"],
  "Waiting for {origin} to release the conversation…": ["Esperando a que {origin} libere la conversación…", "En attente que {origin} libère la conversation…", "Warten, bis die Unterhaltung in {origin} freigegeben wird…", "{origin} が会話を解放するのを待っています…", "正在等待 {origin} 释放对话…"],
  "Stop the run": ["Detener la ejecución", "Arrêter l’exécution", "Ausführung anhalten", "実行を停止", "停止运行"],
  "Sending your choice…": ["Enviando tu elección…", "Envoi de votre choix…", "Deine Auswahl wird gesendet…", "選択を送信中…", "正在发送你的选择…"],
  "Your choice was not taken: the question had ended or changed.": ["Tu elección no se aplicó: la pregunta había terminado o cambiado.", "Votre choix n’a pas été pris en compte : la question était terminée ou avait changé.", "Deine Auswahl wurde nicht übernommen: Die Frage war beendet oder hatte sich geändert.", "選択は反映されませんでした。質問は終了したか変更されていました。", "你的选择未被采用：该问题已结束或已更改。"],
  "Whether your choice was taken is not known: what still waits is shown.": ["No se sabe si tu elección se aplicó: se muestra lo que sigue pendiente.", "On ne sait pas si votre choix a été pris en compte : ce qui attend encore est affiché.", "Ob deine Auswahl übernommen wurde, ist nicht bekannt: Angezeigt wird, was noch wartet.", "選択が反映されたかは不明です。まだ待機中のものを表示しています。", "无法确定你的选择是否已被采用：显示的是仍在等待的内容。"],
  "The question of the held conversation is unavailable ({code}).": ["La pregunta sobre la conversación ocupada no está disponible ({code}).", "La question sur la conversation occupée est indisponible ({code}).", "Die Frage zur belegten Unterhaltung ist nicht verfügbar ({code}).", "使用中の会話についての質問を表示できません ({code})。", "关于被占用对话的问题不可用（{code}）。"],
  "Reload the window to answer it.": ["Recarga la ventana para responder.", "Rechargez la fenêtre pour y répondre.", "Lade das Fenster neu, um sie zu beantworten.", "回答するにはウィンドウを再読み込みしてください。", "请重新加载窗口以回答。"],
  // Where the other process runs.
  "VS Code": ["VS Code", "VS Code", "VS Code", "VS Code", "VS Code"],
  "a terminal": ["una terminal", "un terminal", "einem Terminal", "ターミナル", "终端"],
  "a background session of Claude Code": ["una sesión en segundo plano de Claude Code", "une session d’arrière-plan de Claude Code", "einer Hintergrundsitzung von Claude Code", "Claude Code のバックグラウンドセッション", "Claude Code 的后台会话"],
  "another application": ["otra aplicación", "une autre application", "einer anderen Anwendung", "別のアプリケーション", "另一个应用程序"],
  "another Claude process": ["otro proceso de Claude", "un autre processus Claude", "einem anderen Claude-Prozess", "別の Claude プロセス", "另一个 Claude 进程"],
} as const;

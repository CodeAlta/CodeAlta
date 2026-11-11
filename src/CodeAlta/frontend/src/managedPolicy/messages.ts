// English source keys; translations in es, fr, de, ja, zh-CN order.
export const managedPolicyMessages = {
  // The page of a Copilot provider, and the settings the policy locks.
  "Policy of your organization": ["Política de tu organización", "Politique de votre organisation", "Richtlinie Ihrer Organisation", "組織のポリシー", "组织策略"],
  "Refresh the policy": ["Actualizar la política", "Actualiser la politique", "Richtlinie aktualisieren", "ポリシーを更新", "刷新策略"],
  "Set by your organization": ["Establecido por tu organización", "Défini par votre organisation", "Von Ihrer Organisation festgelegt", "組織が設定しています", "由你的组织设置"],
  "Who to ask": ["A quién preguntar", "Qui contacter", "Ansprechpartner", "問い合わせ先", "联系谁"],
  "No policy of your organization applies.": ["No se aplica ninguna política de tu organización.", "Aucune politique de votre organisation ne s’applique.", "Keine Richtlinie Ihrer Organisation gilt.", "組織のポリシーは適用されていません。", "没有适用的组织策略。"],
  "Bypass permissions is turned off.": ["Omitir permisos está desactivado.", "Contourner les autorisations est désactivé.", "„Berechtigungen umgehen“ ist deaktiviert.", "「権限をバイパス」はオフです。", "“绕过权限”已关闭。"],
  "Auto is turned off.": ["Auto está desactivado.", "Auto est désactivé.", "„Auto“ ist deaktiviert.", "「自動」はオフです。", "“自动”已关闭。"],
  "New sessions start in {mode}.": ["Las sesiones nuevas empiezan en {mode}.", "Les nouvelles sessions démarrent en {mode}.", "Neue Sitzungen starten in {mode}.", "新しいセッションは {mode} で始まります。", "新会话以 {mode} 开始。"],
  "Every session runs in the sandbox.": ["Cada sesión se ejecuta en el sandbox.", "Chaque session s’exécute dans le bac à sable.", "Jede Sitzung läuft in der Sandbox.", "すべてのセッションはサンドボックスで実行されます。", "每个会话都在沙箱中运行。"],
  "No command runs outside the sandbox.": ["Ningún comando se ejecuta fuera del sandbox.", "Aucune commande ne s’exécute hors du bac à sable.", "Kein Befehl läuft außerhalb der Sandbox.", "サンドボックスの外ではコマンドを実行しません。", "没有命令在沙箱外运行。"],
  "No session runs until the policy is read again.": ["Ninguna sesión se ejecuta hasta que se vuelva a leer la política.", "Aucune session ne s’exécute avant une nouvelle lecture de la politique.", "Keine Sitzung läuft, bis die Richtlinie erneut gelesen wurde.", "ポリシーを再度読み込むまで、セッションは実行されません。", "在重新读取策略之前，任何会话都不会运行。"],
  "Downloading the Copilot runtime…": ["Descargando el runtime de Copilot…", "Téléchargement du runtime de Copilot…", "Copilot-Laufzeit wird heruntergeladen…", "Copilot ランタイムをダウンロード中…", "正在下载 Copilot 运行时…"],
  "{received} of {total} MB": ["{received} de {total} MB", "{received} sur {total} Mo", "{received} von {total} MB", "{received} / {total} MB", "{received} / {total} MB"],
  "Reading the policy of your organization…": ["Leyendo la política de tu organización…", "Lecture de la politique de votre organisation…", "Richtlinie Ihrer Organisation wird gelesen…", "組織のポリシーを読み込み中…", "正在读取组织策略…"],
  "The policy of your organization could not be read.": ["No se pudo leer la política de tu organización.", "La politique de votre organisation n’a pas pu être lue.", "Die Richtlinie Ihrer Organisation konnte nicht gelesen werden.", "組織のポリシーを読み込めませんでした。", "无法读取组织策略。"],
  "read {time}": ["leída a las {time}", "lue à {time}", "gelesen um {time}", "{time} に読み込み", "{time} 读取"],
  // The composer, for a prompt the policy refused.
  "The policy of your organization stopped this prompt: {message}": ["La política de tu organización detuvo este prompt: {message}", "La politique de votre organisation a arrêté ce prompt : {message}", "Die Richtlinie Ihrer Organisation hat diesen Prompt gestoppt: {message}", "組織のポリシーがこのプロンプトを止めました: {message}", "组织策略阻止了此提示：{message}"],
} as const;

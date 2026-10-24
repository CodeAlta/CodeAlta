// English source keys; translations in es, fr, de, ja, zh-CN order.
export const pluginProviderMessages = {
  // The catalog of a provider type of a plugin: the agents of the ACP registry.
  "{title}…": ["{title}…", "{title}…", "{title}…", "{title}…", "{title}…"],
  "Search the agents": ["Buscar agentes", "Rechercher des agents", "Agenten suchen", "エージェントを検索", "搜索代理"],
  "Add": ["Añadir", "Ajouter", "Hinzufügen", "追加", "添加"],
  "Adding…": ["Añadiendo…", "Ajout…", "Wird hinzugefügt…", "追加中…", "正在添加…"],
  "No agent matches.": ["Ningún agente coincide.", "Aucun agent ne correspond.", "Kein Agent passt.", "一致するエージェントはありません。", "没有匹配的代理。"],
  "Loading the agents.": ["Cargando los agentes.", "Chargement des agents.", "Agenten werden geladen.", "エージェントを読み込んでいます。", "正在加载代理。"],
  "The catalog could not be read.": ["No se pudo leer el catálogo.", "Le catalogue n’a pas pu être lu.", "Der Katalog konnte nicht gelesen werden.", "カタログを読み取れませんでした。", "无法读取目录。"],
  "Showing the copy kept from {date}.": ["Se muestra la copia guardada del {date}.", "Copie conservée du {date}.", "Gezeigt wird die Kopie vom {date}.", "{date} に保存したコピーを表示しています。", "显示 {date} 保存的副本。"],
  "{name} was added.": ["Se añadió {name}.", "{name} a été ajouté.", "{name} wurde hinzugefügt.", "{name} を追加しました。", "已添加 {name}。"],
  "{name} could not be added: {reason}": ["No se pudo añadir {name}: {reason}", "{name} n’a pas pu être ajouté : {reason}", "{name} konnte nicht hinzugefügt werden: {reason}", "{name} を追加できませんでした: {reason}", "无法添加 {name}：{reason}"],
  "{received} of {total}": ["{received} de {total}", "{received} sur {total}", "{received} von {total}", "{total} 中 {received}", "{received} / {total}"],
  // The form of a provider of a plugin.
  "A line of the environment has no name before =.": ["Una línea del entorno no tiene nombre antes de =.", "Une ligne de l’environnement n’a pas de nom avant =.", "Eine Zeile der Umgebung hat keinen Namen vor =.", "環境の行に = の前の名前がありません。", "环境中有一行在 = 之前没有名称。"],
  "One per line.": ["Uno por línea.", "Un par ligne.", "Einer pro Zeile.", "1 行に 1 つ。", "每行一个。"],
  "NAME=value, one per line.": ["NOMBRE=valor, uno por línea.", "NOM=valeur, un par ligne.", "NAME=Wert, einer pro Zeile.", "NAME=値、1 行に 1 つ。", "NAME=值，每行一个。"],
  "Update to {version}": ["Actualizar a {version}", "Mettre à jour vers {version}", "Auf {version} aktualisieren", "{version} に更新", "更新到 {version}"],
  "Updating…": ["Actualizando…", "Mise à jour…", "Wird aktualisiert…", "更新中…", "正在更新…"],
  "Sign in": ["Iniciar sesión", "Se connecter", "Anmelden", "サインイン", "登录"],
  "Signing in…": ["Iniciando sesión…", "Connexion…", "Anmeldung…", "サインイン中…", "正在登录…"],
  "Run in a terminal": ["Ejecutar en un terminal", "Exécuter dans un terminal", "In einem Terminal ausführen", "ターミナルで実行", "在终端中运行"],
  "Run this command in a terminal to sign in, then test the provider again.": ["Ejecuta este comando en un terminal para iniciar sesión y vuelve a probar el proveedor.", "Exécutez cette commande dans un terminal pour vous connecter, puis testez à nouveau le fournisseur.", "Diesen Befehl in einem Terminal ausführen, um sich anzumelden, und den Anbieter dann erneut testen.", "サインインするにはターミナルでこのコマンドを実行し、プロバイダーをもう一度テストしてください。", "在终端中运行此命令进行登录，然后再次测试提供商。"],
  "Signed in. Test the provider to see its models.": ["Sesión iniciada. Prueba el proveedor para ver sus modelos.", "Connecté. Testez le fournisseur pour voir ses modèles.", "Angemeldet. Den Anbieter testen, um seine Modelle zu sehen.", "サインインしました。モデルを表示するにはプロバイダーをテストしてください。", "已登录。测试提供商以查看其模型。"],
  "The sign-in did not complete: {reason}": ["El inicio de sesión no terminó: {reason}", "La connexion n’a pas abouti : {reason}", "Die Anmeldung wurde nicht abgeschlossen: {reason}", "サインインが完了しませんでした: {reason}", "登录未完成：{reason}"],
  "Updated to {version}.": ["Actualizado a {version}.", "Mis à jour vers {version}.", "Auf {version} aktualisiert.", "{version} に更新しました。", "已更新到 {version}。"],
  "The update did not complete: {reason}": ["La actualización no terminó: {reason}", "La mise à jour n’a pas abouti : {reason}", "Die Aktualisierung wurde nicht abgeschlossen: {reason}", "更新が完了しませんでした: {reason}", "更新未完成：{reason}"],
  "Save the provider, then sign in and test it here.": ["Guarda el proveedor y luego inicia sesión y pruébalo aquí.", "Enregistrez le fournisseur, puis connectez-vous et testez-le ici.", "Den Anbieter speichern, dann hier anmelden und testen.", "プロバイダーを保存してから、ここでサインインしてテストしてください。", "保存提供商，然后在此登录并测试。"],
  // What a provider of a plugin can and cannot do.
  "What it can do": ["Lo que puede hacer", "Ce qu’il peut faire", "Was er kann", "できること", "功能"],
  "Test the provider to see what it can do.": ["Prueba el proveedor para ver lo que puede hacer.", "Testez le fournisseur pour voir ce qu’il peut faire.", "Den Anbieter testen, um zu sehen, was er kann.", "できることを確認するにはプロバイダーをテストしてください。", "测试提供商以查看其功能。"],
  // The reasons a provider of a plugin is not ready.
  "{provider} is not signed in. Sign in from its form, then test again.": ["{provider} no ha iniciado sesión. Inicia sesión desde su formulario y vuelve a probar.", "{provider} n’est pas connecté. Connectez-vous depuis son formulaire, puis testez à nouveau.", "{provider} ist nicht angemeldet. Über sein Formular anmelden und dann erneut testen.", "{provider} はサインインしていません。フォームからサインインして、もう一度テストしてください。", "{provider} 未登录。请在其表单中登录，然后再次测试。"],
  "The command of {provider} was not found. Install the agent, or the tool that runs it, then test again.": ["No se encontró el comando de {provider}. Instala el agente, o la herramienta que lo ejecuta, y vuelve a probar.", "La commande de {provider} est introuvable. Installez l’agent, ou l’outil qui l’exécute, puis testez à nouveau.", "Der Befehl von {provider} wurde nicht gefunden. Den Agenten oder das Werkzeug, das ihn ausführt, installieren und dann erneut testen.", "{provider} のコマンドが見つかりません。エージェント、またはそれを実行するツールをインストールして、もう一度テストしてください。", "找不到 {provider} 的命令。请安装该代理或运行它的工具，然后再次测试。"],
  "{provider} did not start or did not answer. Check its command, then test again.": ["{provider} no se inició o no respondió. Revisa su comando y vuelve a probar.", "{provider} n’a pas démarré ou n’a pas répondu. Vérifiez sa commande, puis testez à nouveau.", "{provider} ist nicht gestartet oder hat nicht geantwortet. Seinen Befehl prüfen und dann erneut testen.", "{provider} が起動しなかったか、応答しませんでした。コマンドを確認して、もう一度テストしてください。", "{provider} 未启动或未响应。请检查其命令，然后再次测试。"],
} as const;

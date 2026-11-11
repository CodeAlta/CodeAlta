// The words of the sign-ins of a machine (Settings > Fleet > Machines > a machine > Sign-ins), in the five languages after
// English (es, fr, de, ja, zh-CN), merged into the dictionary of `localization.ts`. English is the key.
type Row = readonly [string, string, string, string, string];

export const machineSignInMessages = {
  "Sign-ins": ["Inicios de sesión", "Connexions", "Anmeldungen", "サインイン", "登录"],
  "What {name} signs in to by itself: its providers and its MCP servers. The page opens in the browser of this computer; the tokens stay on {name}.": [
    "Lo que {name} inicia por sí misma: sus proveedores y sus servidores MCP. La página se abre en el navegador de este equipo; los tokens se quedan en {name}.",
    "Ce à quoi {name} se connecte elle-même : ses fournisseurs et ses serveurs MCP. La page s’ouvre dans le navigateur de cet ordinateur ; les jetons restent sur {name}.",
    "Wo sich {name} selbst anmeldet: ihre Anbieter und ihre MCP-Server. Die Seite öffnet sich im Browser dieses Computers; die Token bleiben auf {name}.",
    "{name} が自らサインインするもの：プロバイダーと MCP サーバー。ページはこのコンピューターのブラウザーで開き、トークンは {name} に残ります。",
    "{name} 自行登录的内容：其提供方和 MCP 服务器。页面在此计算机的浏览器中打开；令牌保留在 {name} 上。"],
  "Nothing of {name} signs in from here.": ["Nada de {name} inicia sesión desde aquí.", "Rien de {name} ne se connecte d’ici.", "Nichts auf {name} meldet sich von hier aus an.",
    "{name} にはここからサインインするものがありません。", "{name} 没有可从此处登录的内容。"],
  "as {account}": ["como {account}", "en tant que {account}", "als {account}", "{account} として", "身份为 {account}"],
  "Finish the sign-in in the browser that opened on this computer.": ["Termina el inicio de sesión en el navegador que se abrió en este equipo.",
    "Terminez la connexion dans le navigateur qui s’est ouvert sur cet ordinateur.", "Schließe die Anmeldung im Browser ab, der sich auf diesem Computer geöffnet hat.",
    "このコンピューターで開いたブラウザーでサインインを完了してください。", "请在此计算机上打开的浏览器中完成登录。"],
  "Sign in on the page that opened in your browser, then paste the code it shows:": ["Inicia sesión en la página que se abrió en tu navegador y pega el código que muestra:",
    "Connectez-vous sur la page qui s’est ouverte dans votre navigateur, puis collez le code qu’elle affiche :",
    "Melde dich auf der Seite an, die sich in deinem Browser geöffnet hat, und füge den angezeigten Code ein:",
    "ブラウザーで開いたページでサインインし、表示されたコードを貼り付けてください：", "在浏览器中打开的页面上登录，然后粘贴其显示的代码："],
  "The browser of this computer did not open it: copy the address into your browser.": ["El navegador de este equipo no la abrió: copia la dirección en tu navegador.",
    "Le navigateur de cet ordinateur ne l’a pas ouverte : copiez l’adresse dans votre navigateur.", "Der Browser dieses Computers hat sie nicht geöffnet: Kopiere die Adresse in deinen Browser.",
    "このコンピューターのブラウザーで開けませんでした。アドレスをブラウザーにコピーしてください。", "此计算机的浏览器未能打开它：请将地址复制到浏览器中。"],
  "Only a computer paired with {name} signs it in. To do it from this one, run altanode pair --manage on {name}, then paste the link in Pair a machine… here.": [
    "Solo un equipo emparejado con {name} inicia su sesión. Para hacerlo desde este, ejecuta altanode pair --manage en {name} y pega el enlace en Emparejar una máquina… aquí.",
    "Seul un ordinateur associé à {name} la connecte. Pour le faire depuis celui-ci, lancez altanode pair --manage sur {name}, puis collez le lien dans Associer une machine… ici.",
    "Nur ein mit {name} gekoppelter Computer meldet sie an. Um es von diesem aus zu tun, führe altanode pair --manage auf {name} aus und füge den Link hier in Maschine koppeln… ein.",
    "{name} をサインインできるのは、{name} とペアリングしたコンピューターだけです。このコンピューターから行うには、{name} で altanode pair --manage を実行し、ここの「マシンをペアリング…」にリンクを貼り付けてください。",
    "只有与 {name} 配对的计算机才能为其登录。要从这台计算机操作，请在 {name} 上运行 altanode pair --manage，然后将链接粘贴到此处的“配对机器…”中。"],
  "The sign-in did not complete on the machine.": ["El inicio de sesión no se completó en la máquina.", "La connexion ne s’est pas terminée sur la machine.",
    "Die Anmeldung wurde auf der Maschine nicht abgeschlossen.", "マシンでサインインが完了しませんでした。", "登录未在该机器上完成。"],
  "The sign-in took too long: start it again.": ["El inicio de sesión tardó demasiado: vuelve a empezarlo.", "La connexion a pris trop de temps : recommencez-la.",
    "Die Anmeldung hat zu lange gedauert: Starte sie erneut.", "サインインに時間がかかりすぎました。もう一度開始してください。", "登录耗时过长：请重新开始。"],
  "Type it here: {machine} keeps it as a secret, and it never comes back to this computer.": [
    "Escríbelo aquí: {machine} lo guarda como secreto y nunca vuelve a este equipo.",
    "Saisissez-la ici : {machine} la garde comme secret, et elle ne revient jamais sur cet ordinateur.",
    "Gib ihn hier ein: {machine} bewahrt ihn als Geheimnis auf, und er kommt nie auf diesen Computer zurück.",
    "ここに入力してください。{machine} はシークレットとして保管し、このコンピューターに戻ることはありません。",
    "在此输入：{machine} 会将其保存为机密，且绝不会传回此计算机。"],
  "Where to get it": ["Dónde obtenerlo", "Où l’obtenir", "Wo du ihn bekommst", "取得方法", "在哪里获取"],
} as const satisfies Record<string, Row>;
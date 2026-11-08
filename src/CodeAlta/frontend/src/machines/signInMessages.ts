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
  "Open this page, then paste the code it shows:": ["Abre esta página y pega el código que muestra:", "Ouvrez cette page, puis collez le code qu’elle affiche :",
    "Öffne diese Seite und füge den angezeigten Code ein:", "このページを開き、表示されたコードを貼り付けてください：", "打开此页面，然后粘贴其显示的代码："],
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

// The words of the Mobile tab, in the five languages after English (es, fr, de, ja, zh-CN), merged into the dictionary of
// `localization.ts`. English is the key. "Try again", "Copy address" and "Copied" are the window's own.
type Row = readonly [string, string, string, string, string];

export const mobileCanvasMessages = {
  "Scan it with the camera of the phone. The phone must be signed in to the same tailnet.": [
    "Escanéalo con la cámara del teléfono. El teléfono debe haber iniciado sesión en la misma tailnet.",
    "Scannez-le avec l’appareil photo du téléphone. Le téléphone doit être connecté au même tailnet.",
    "Scannen Sie ihn mit der Kamera des Telefons. Das Telefon muss im selben Tailnet angemeldet sein.",
    "スマートフォンのカメラでスキャンしてください。スマートフォンは同じ tailnet にサインインしている必要があります。",
    "用手机相机扫描。手机必须登录到同一个 tailnet。"],
  "QR code of the link of the phone page": ["Código QR del enlace de la página del teléfono", "Code QR du lien de la page du téléphone", "QR-Code des Links der Telefonseite", "スマートフォン用ページのリンクの QR コード", "手机页面链接的二维码"],
  "The phone page is starting.": ["La página del teléfono se está iniciando.", "La page du téléphone démarre.", "Die Telefonseite wird gestartet.", "スマートフォン用ページを起動しています。", "手机页面正在启动。"],
  "Tailscale is starting to publish the page.": ["Tailscale está empezando a publicar la página.", "Tailscale commence à publier la page.", "Tailscale beginnt, die Seite zu veröffentlichen.", "Tailscale がページの公開を始めています。", "Tailscale 正在开始发布页面。"],
  "The port {port} is in use by another program.": ["El puerto {port} lo usa otro programa.", "Le port {port} est utilisé par un autre programme.", "Der Port {port} wird von einem anderen Programm verwendet.", "ポート {port} は別のプログラムが使用しています。", "端口 {port} 正被另一个程序使用。"],
  "Another CodeAlta with the Mobile plugin on may be using it.": ["Puede que lo use otro CodeAlta con el complemento Mobile activado.", "Un autre CodeAlta dont l’extension Mobile est activée l’utilise peut-être.",
    "Möglicherweise verwendet ihn ein anderes CodeAlta mit aktiviertem Mobile-Plugin.", "Mobile プラグインが有効な別の CodeAlta が使用している可能性があります。", "可能是另一个启用了 Mobile 插件的 CodeAlta 正在使用它。"],
  "The server of the phone page did not start.": ["El servidor de la página del teléfono no se inició.", "Le serveur de la page du téléphone n’a pas démarré.", "Der Server der Telefonseite wurde nicht gestartet.", "スマートフォン用ページのサーバーが起動しませんでした。", "手机页面的服务器未能启动。"],
  "The server of the phone page is stopped.": ["El servidor de la página del teléfono está detenido.", "Le serveur de la page du téléphone est arrêté.", "Der Server der Telefonseite ist angehalten.", "スマートフォン用ページのサーバーは停止しています。", "手机页面的服务器已停止。"],
  "Tailscale is not installed on this computer.": ["Tailscale no está instalado en este equipo.", "Tailscale n’est pas installé sur cet ordinateur.", "Tailscale ist auf diesem Computer nicht installiert.", "このコンピューターには Tailscale がインストールされていません。", "此电脑未安装 Tailscale。"],
  "Tailscale is not running on this computer.": ["Tailscale no se está ejecutando en este equipo.", "Tailscale ne fonctionne pas sur cet ordinateur.", "Tailscale läuft auf diesem Computer nicht.", "このコンピューターで Tailscale が実行されていません。", "此电脑上的 Tailscale 未在运行。"],
  "Tailscale Serve is not enabled for this tailnet.": ["Tailscale Serve no está activado para esta tailnet.", "Tailscale Serve n’est pas activé pour ce tailnet.", "Tailscale Serve ist für dieses Tailnet nicht aktiviert.", "この tailnet では Tailscale Serve が有効になっていません。", "此 tailnet 未启用 Tailscale Serve。"],
  "The port of Tailscale Serve is used by another service.": ["El puerto de Tailscale Serve lo usa otro servicio.", "Le port de Tailscale Serve est utilisé par un autre service.", "Der Port von Tailscale Serve wird von einem anderen Dienst verwendet.", "Tailscale Serve のポートは別のサービスが使用しています。", "Tailscale Serve 的端口正被另一个服务使用。"],
  "Tailscale could not publish the page.": ["Tailscale no pudo publicar la página.", "Tailscale n’a pas pu publier la page.", "Tailscale konnte die Seite nicht veröffentlichen.", "Tailscale はページを公開できませんでした。", "Tailscale 无法发布页面。"],
  "The page is not published on the tailnet.": ["La página no está publicada en la tailnet.", "La page n’est pas publiée sur le tailnet.", "Die Seite ist im Tailnet nicht veröffentlicht.", "ページは tailnet に公開されていません。", "页面未发布到 tailnet。"],
  "Download Tailscale": ["Descargar Tailscale", "Télécharger Tailscale", "Tailscale herunterladen", "Tailscale をダウンロード", "下载 Tailscale"],
  "Enable it in the admin console": ["Activarlo en la consola de administración", "L’activer dans la console d’administration", "In der Administratorkonsole aktivieren", "管理コンソールで有効にする", "在管理控制台中启用"],
  "Open Tailscale": ["Abrir Tailscale", "Ouvrir Tailscale", "Tailscale öffnen", "Tailscale を開く", "打开 Tailscale"],
  "Open the page that fixes it": ["Abrir la página que lo soluciona", "Ouvrir la page qui le corrige", "Seite zur Behebung öffnen", "解決するページを開く", "打开解决此问题的页面"],
  "The Mobile plugin does not answer.": ["El complemento Mobile no responde.", "L’extension Mobile ne répond pas.", "Das Mobile-Plugin antwortet nicht.", "Mobile プラグインが応答しません。", "Mobile 插件没有响应。"],
  "The address could not be copied.": ["No se pudo copiar la dirección.", "L’adresse n’a pas pu être copiée.", "Die Adresse konnte nicht kopiert werden.", "アドレスをコピーできませんでした。", "无法复制地址。"],
} satisfies Record<string, Row>;

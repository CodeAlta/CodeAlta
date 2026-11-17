// The tabs and the groups of the form of a provider, and the rows of its environment: English source keys, with the five
// languages after English (es, fr, de, ja, zh-CN), merged into the dictionary of `localization.ts`. English is the key.
export const providerFormMessages = {
  "Permissions and limits": ["Permisos y límites", "Permissions et limites", "Berechtigungen und Limits", "権限と上限", "权限与限制"],
  "Save the provider to choose its sandbox, its Code Mode and its spaces.": [
    "Guarda el proveedor para elegir su sandbox, su Code Mode y sus espacios.",
    "Enregistrez le fournisseur pour choisir son bac à sable, son Code Mode et ses espaces.",
    "Den Anbieter speichern, um seine Sandbox, seinen Code Mode und seine Spaces zu wählen.",
    "サンドボックス、Code Mode、スペースを選ぶには、プロバイダーを保存してください。",
    "保存提供商后即可选择其沙箱、Code Mode 和空间。",
  ],
  "Model list": ["Lista de modelos", "Liste des modèles", "Modellliste", "モデル一覧", "模型列表"],
  "Open the configuration file": ["Abrir el archivo de configuración", "Ouvrir le fichier de configuration", "Konfigurationsdatei öffnen", "設定ファイルを開く", "打开配置文件"],
  "Variables the CLI of this provider starts with. In a sandbox on macOS, a token of its account (CLAUDE_CODE_OAUTH_TOKEN, made with claude setup-token) signs it in: keep it as a secret, with the key button of its row.": [
    "Variables con las que se inicia la CLI de este proveedor. En un sandbox en macOS, un token de su cuenta (CLAUDE_CODE_OAUTH_TOKEN, creado con claude setup-token) inicia su sesión: guárdalo como secreto, con el botón de llave de su fila.",
    "Variables avec lesquelles la CLI de ce fournisseur démarre. Dans un bac à sable sur macOS, un jeton de son compte (CLAUDE_CODE_OAUTH_TOKEN, créé avec claude setup-token) le connecte : gardez-le comme secret, avec le bouton clé de sa ligne.",
    "Variablen, mit denen die CLI dieses Anbieters startet. In einer Sandbox unter macOS meldet ein Token seines Kontos (CLAUDE_CODE_OAUTH_TOKEN, erstellt mit claude setup-token) sie an: als Geheimnis aufbewahren, mit der Schlüssel-Schaltfläche seiner Zeile.",
    "このプロバイダーの CLI の起動時に設定する変数です。macOS のサンドボックスでは、アカウントのトークン (CLAUDE_CODE_OAUTH_TOKEN、claude setup-token で作成) でサインインします。行の鍵ボタンでシークレットとして保管してください。",
    "此提供商的 CLI 启动时使用的变量。在 macOS 的沙箱中，其帐户的令牌 (CLAUDE_CODE_OAUTH_TOKEN，用 claude setup-token 创建) 用于登录：请用该行的钥匙按钮将其保存为机密。",
  ],
  // The rows of the environment of a provider, where a value can be a secret of this computer.
  "Use a secret": ["Usar un secreto", "Utiliser un secret", "Geheimnis verwenden", "シークレットを使う", "使用机密"],
  "Use a secret or a vault item as the value": [
    "Usar un secreto o un elemento de un almacén como valor", "Utiliser un secret ou un élément de coffre comme valeur",
    "Ein Geheimnis oder einen Tresoreintrag als Wert verwenden", "シークレットまたは保管庫の項目を値として使う", "使用机密或保管库项目作为值",
  ],
  "The value is read from this secret when the provider starts. It is never shown here.": [
    "El valor se lee de este secreto cuando se inicia el proveedor. Nunca se muestra aquí.",
    "La valeur est lue dans ce secret au démarrage du fournisseur. Elle n’est jamais affichée ici.",
    "Der Wert wird beim Start des Anbieters aus diesem Geheimnis gelesen. Er wird hier nie angezeigt.",
    "値はプロバイダーの起動時にこのシークレットから読み取られます。ここには表示されません。",
    "提供商启动时从此机密读取该值，此处从不显示。",
  ],
  "Not set on this computer": ["No definido en este equipo", "Non défini sur cet ordinateur", "Auf diesem Computer nicht gesetzt", "このコンピューターでは未設定", "此计算机上未设置"],
  "Type a value instead": ["Escribir un valor en su lugar", "Saisir une valeur à la place", "Stattdessen einen Wert eingeben", "代わりに値を入力", "改为输入值"],
  "No secret on this computer yet": ["Aún no hay secretos en este equipo", "Aucun secret sur cet ordinateur pour l’instant", "Noch kein Geheimnis auf diesem Computer", "このコンピューターにはまだシークレットがありません", "此计算机上尚无机密"],
  "The secrets could not be read.": ["No se pudieron leer los secretos.", "Les secrets n’ont pas pu être lus.", "Die Geheimnisse konnten nicht gelesen werden.", "シークレットを読み取れませんでした。", "无法读取机密。"],
  // What the form says where a choice is not part of Save and apply, and where a key can be kept once the provider is saved.
  "Saved at once": ["Se guarda al instante", "Enregistré aussitôt", "Sofort gespeichert", "すぐに保存", "立即保存"],
  "This choice is saved as soon as it is made: Save and apply and Revert do not change it.": [
    "Esta elección se guarda en cuanto se hace: Guardar y aplicar y Revertir no la cambian.",
    "Ce choix est enregistré dès qu’il est fait : Enregistrer et appliquer et Rétablir ne le changent pas.",
    "Diese Auswahl wird sofort gespeichert: Speichern und anwenden sowie Zurücksetzen ändern sie nicht.",
    "この選択はすぐに保存されます。[保存して適用] と [元に戻す] では変わりません。",
    "此选择一经做出即保存：“保存并应用”和“还原”不会更改它。",
  ],
  "Once the provider is saved, its key can be kept as a secret or taken from a vault here.": [
    "Una vez guardado el proveedor, su clave puede guardarse aquí como secreto o tomarse de un almacén.",
    "Une fois le fournisseur enregistré, sa clé peut être gardée ici comme secret ou prise dans un coffre.",
    "Sobald der Anbieter gespeichert ist, kann sein Schlüssel hier als Geheimnis aufbewahrt oder aus einem Tresor übernommen werden.",
    "プロバイダーを保存すると、ここでキーをシークレットとして保管するか、保管庫から取得できます。",
    "保存提供商后，可在此将其密钥保存为机密或从保管库获取。",
  ],
  "Secrets and vaults are added in Settings > Secrets.": [
    "Los secretos y los almacenes se añaden en Ajustes > Secretos.", "Les secrets et les coffres s’ajoutent dans Paramètres > Secrets.",
    "Geheimnisse und Tresore werden unter Einstellungen > Geheimnisse hinzugefügt.", "シークレットと保管庫は [設定] > [シークレット] で追加します。", "机密和保管库在“设置 > 机密”中添加。",
  ],
} as const;

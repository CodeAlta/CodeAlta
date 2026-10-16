// English source keys; translations in es, fr, de, ja, zh-CN order.
export const externalEditorMessages = {
  // The actions, named by the editor in use and by the file manager of the system.
  "Open in {editor}": ["Abrir en {editor}", "Ouvrir dans {editor}", "In {editor} öffnen", "{editor} で開く", "在 {editor} 中打开"],
  "Open {name} in {editor}": ["Abrir {name} en {editor}", "Ouvrir {name} dans {editor}", "{name} in {editor} öffnen", "{name} を {editor} で開く", "在 {editor} 中打开 {name}"],
  "Open in File Explorer": ["Abrir en el Explorador de archivos", "Ouvrir dans l’Explorateur de fichiers", "Im Datei-Explorer öffnen", "エクスプローラーで開く", "在文件资源管理器中打开"],
  "Open in Finder": ["Abrir en Finder", "Ouvrir dans le Finder", "Im Finder öffnen", "Finder で開く", "在访达中打开"],
  "Open in the file manager": ["Abrir en el administrador de archivos", "Ouvrir dans le gestionnaire de fichiers", "Im Dateimanager öffnen", "ファイルマネージャーで開く", "在文件管理器中打开"],
  "Open {name} in File Explorer": ["Abrir {name} en el Explorador de archivos", "Ouvrir {name} dans l’Explorateur de fichiers", "{name} im Datei-Explorer öffnen", "{name} をエクスプローラーで開く", "在文件资源管理器中打开 {name}"],
  "Open {name} in Finder": ["Abrir {name} en Finder", "Ouvrir {name} dans le Finder", "{name} im Finder öffnen", "{name} を Finder で開く", "在访达中打开 {name}"],
  "Open {name} in the file manager": ["Abrir {name} en el administrador de archivos", "Ouvrir {name} dans le gestionnaire de fichiers", "{name} im Dateimanager öffnen", "{name} をファイルマネージャーで開く", "在文件管理器中打开 {name}"],
  // What went wrong.
  "No external editor was found. Choose one in Settings > General.": [
    "No se encontró ningún editor externo. Elige uno en Configuración > General.",
    "Aucun éditeur externe n’a été trouvé. Choisissez-en un dans Paramètres > Général.",
    "Es wurde kein externer Editor gefunden. Wählen Sie einen unter Einstellungen > Allgemein.",
    "外部エディターが見つかりませんでした。設定 > 全般 で選択してください。",
    "未找到外部编辑器。请在 设置 > 常规 中选择一个。"],
  "The custom editor cannot be started: choose a program that exists and is not a batch file.": [
    "No se puede iniciar el editor personalizado: elige un programa que exista y que no sea un archivo por lotes.",
    "L’éditeur personnalisé ne peut pas être lancé : choisissez un programme qui existe et qui n’est pas un fichier de commandes.",
    "Der eigene Editor kann nicht gestartet werden: Wählen Sie ein vorhandenes Programm, das keine Batchdatei ist.",
    "カスタム エディターを起動できません。存在する、バッチ ファイルではないプログラムを選択してください。",
    "无法启动自定义编辑器：请选择一个存在且不是批处理文件的程序。"],
  "The external editor could not be started.": [
    "No se pudo iniciar el editor externo.", "L’éditeur externe n’a pas pu être lancé.", "Der externe Editor konnte nicht gestartet werden.",
    "外部エディターを起動できませんでした。", "无法启动外部编辑器。"],
  "The folder could not be opened.": ["No se pudo abrir la carpeta.", "Le dossier n’a pas pu être ouvert.", "Der Ordner konnte nicht geöffnet werden.", "フォルダーを開けませんでした。", "无法打开该文件夹。"],
  // The setting, on the General page.
  "External editor": ["Editor externo", "Éditeur externe", "Externer Editor", "外部エディター", "外部编辑器"],
  "{editor} (found)": ["{editor} (encontrado)", "{editor} (trouvé)", "{editor} (gefunden)", "{editor} (検出)", "{editor}（已找到）"],
  "Custom…": ["Personalizado…", "Personnalisé…", "Eigener…", "カスタム…", "自定义…"],
  "None found": ["No se encontró ninguno", "Aucun trouvé", "Keiner gefunden", "見つかりません", "未找到"],
  "Detect again": ["Volver a detectar", "Détecter à nouveau", "Erneut suchen", "再検出", "重新检测"],
  "Program": ["Programa", "Programme", "Programm", "プログラム", "程序"],
  "Used by Open in… and /external_editor. The code editor of CodeAlta stays the one that opens files.": [
    "Lo usan Abrir en… y /external_editor. El editor de código de CodeAlta sigue siendo el que abre los archivos.",
    "Utilisé par Ouvrir dans… et /external_editor. L’éditeur de code de CodeAlta reste celui qui ouvre les fichiers.",
    "Wird von „Öffnen in…“ und /external_editor verwendet. Dateien öffnet weiterhin der Code-Editor von CodeAlta.",
    "「…で開く」と /external_editor で使われます。ファイルを開くのは引き続き CodeAlta のコード エディターです。",
    "供“在…中打开”和 /external_editor 使用。打开文件的仍是 CodeAlta 的代码编辑器。"],
  "The full path of the editor's program.": [
    "La ruta completa del programa del editor.", "Le chemin complet du programme de l’éditeur.", "Der vollständige Pfad des Editor-Programms.",
    "エディターのプログラムのフル パス。", "编辑器程序的完整路径。"],
  "{folder}, {file}, {line} and {column} are replaced; an argument with {file} is left out when a folder is opened.": [
    "{folder}, {file}, {line} y {column} se reemplazan; un argumento con {file} se omite cuando se abre una carpeta.",
    "{folder}, {file}, {line} et {column} sont remplacés ; un argument avec {file} est omis quand un dossier est ouvert.",
    "{folder}, {file}, {line} und {column} werden ersetzt; ein Argument mit {file} entfällt, wenn ein Ordner geöffnet wird.",
    "{folder}、{file}、{line}、{column} は置き換えられます。フォルダーを開くときは {file} を含む引数は省かれます。",
    "{folder}、{file}、{line} 和 {column} 会被替换；打开文件夹时，含 {file} 的参数会被省略。"],
  "The arguments have a quote that is not closed.": [
    "Los argumentos tienen unas comillas sin cerrar.", "Les arguments contiennent un guillemet non fermé.", "Die Argumente enthalten ein nicht geschlossenes Anführungszeichen.",
    "引数に閉じられていない引用符があります。", "参数中有未闭合的引号。"],
  "The editor could not be saved.": ["No se pudo guardar el editor.", "L’éditeur n’a pas pu être enregistré.", "Der Editor konnte nicht gespeichert werden.", "エディターを保存できませんでした。", "无法保存编辑器。"],
  // The commands.
  "External Editor": ["Editor externo", "Éditeur externe", "Externer Editor", "外部エディター", "外部编辑器"],
  "File Manager": ["Administrador de archivos", "Gestionnaire de fichiers", "Dateimanager", "ファイル マネージャー", "文件管理器"],
  "Open the folder of the session or of the project in the external editor chosen in Settings.": [
    "Abre la carpeta de la sesión o del proyecto en el editor externo elegido en Configuración.",
    "Ouvre le dossier de la session ou du projet dans l’éditeur externe choisi dans les paramètres.",
    "Öffnet den Ordner der Sitzung oder des Projekts im externen Editor, der in den Einstellungen gewählt ist.",
    "セッションまたはプロジェクトのフォルダーを、設定で選んだ外部エディターで開きます。",
    "在设置中选择的外部编辑器中打开会话或项目的文件夹。"],
  "Show the folder of the session or of the project in the file manager of the system.": [
    "Muestra la carpeta de la sesión o del proyecto en el administrador de archivos del sistema.",
    "Affiche le dossier de la session ou du projet dans le gestionnaire de fichiers du système.",
    "Zeigt den Ordner der Sitzung oder des Projekts im Dateimanager des Systems.",
    "セッションまたはプロジェクトのフォルダーをシステムのファイル マネージャーで表示します。",
    "在系统的文件管理器中显示会话或项目的文件夹。"],
} as const;

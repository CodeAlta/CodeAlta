// English source keys; translations in es, fr, de, ja, zh-CN order.
export const fontMessages = {
  // Settings → Appearance → Fonts.
  "Fonts": ["Fuentes", "Polices", "Schriftarten", "フォント", "字体"],
  "Code font": ["Fuente del código", "Police du code", "Schriftart für Code", "コードのフォント", "代码字体"],
  "Prompt font": ["Fuente del mensaje", "Police de la saisie", "Schriftart der Eingabe", "プロンプトのフォント", "提示词字体"],
  "Interface font": ["Fuente de la interfaz", "Police de l’interface", "Schriftart der Oberfläche", "インターフェイスのフォント", "界面字体"],
  "Installed fonts": ["Fuentes instaladas", "Polices installées", "Installierte Schriftarten", "インストール済みのフォント", "已安装的字体"],
  "Choose an installed font": ["Elegir una fuente instalada", "Choisir une police installée", "Installierte Schriftart wählen", "インストール済みのフォントを選択", "选择已安装的字体"],
  "Monospace fonts": ["Fuentes monoespaciadas", "Polices à chasse fixe", "Nichtproportionale Schriftarten", "等幅フォント", "等宽字体"],
  "Other fonts": ["Otras fuentes", "Autres polices", "Andere Schriftarten", "その他のフォント", "其他字体"],
  "Code blocks, editors and file views. Name installed fonts separated by commas: a character the first one lacks is taken from the next one, then from the built-in {font}.": [
    "Bloques de código, editores y vistas de archivos. Escribe fuentes instaladas separadas por comas: un carácter que le falta a la primera se toma de la siguiente y, por último, de {font}, incluida.",
    "Blocs de code, éditeurs et vues de fichiers. Nommez des polices installées séparées par des virgules : un caractère absent de la première est pris dans la suivante, puis dans {font}, fournie.",
    "Codeblöcke, Editoren und Dateiansichten. Installierte Schriftarten durch Kommas getrennt angeben: Ein Zeichen, das der ersten fehlt, kommt aus der nächsten und zuletzt aus der mitgelieferten {font}.",
    "コード ブロック、エディター、ファイル ビューに使われます。インストール済みのフォントをコンマ区切りで指定します。最初のフォントにない文字は次のフォントから、最後は付属の {font} から取られます。",
    "用于代码块、编辑器和文件视图。输入已安装的字体，用逗号分隔：第一个字体缺少的字符取自下一个字体，最后取自内置的 {font}。"],
  "The prompt is written in the code font, or in the font of the rest of the window.": [
    "El mensaje se escribe con la fuente del código o con la del resto de la ventana.",
    "La saisie s’écrit dans la police du code ou dans celle du reste de la fenêtre.",
    "Die Eingabe wird in der Schriftart für Code oder in der des übrigen Fensters geschrieben.",
    "プロンプトはコードのフォント、またはウィンドウのほかの部分と同じフォントで表示されます。",
    "提示词使用代码字体，或使用窗口其余部分的字体。"],
  "The installed fonts could not be listed: type their names.": [
    "No se pudieron enumerar las fuentes instaladas: escribe sus nombres.",
    "Les polices installées n’ont pas pu être listées : saisissez leurs noms.",
    "Die installierten Schriftarten konnten nicht aufgelistet werden: Geben Sie ihre Namen ein.",
    "インストール済みのフォントを一覧にできませんでした。名前を入力してください。",
    "无法列出已安装的字体：请输入字体名称。"],
} as const satisfies Record<string, readonly [string, string, string, string, string]>;

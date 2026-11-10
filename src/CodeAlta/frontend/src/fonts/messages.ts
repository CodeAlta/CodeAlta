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
  "Code text size": ["Tamaño del texto del código", "Taille du texte du code", "Textgröße für Code", "コードの文字サイズ", "代码文字大小"],
  "Interface text size": ["Tamaño del texto de la interfaz", "Taille du texte de l’interface", "Textgröße der Oberfläche", "インターフェイスの文字サイズ", "界面文字大小"],
  "The size of the text of the code editor, the diffs of changes and reviews, and the prompt, in pixels.": [
    "El tamaño del texto del editor de código, de las diferencias de los cambios y las revisiones, y del mensaje, en píxeles.",
    "La taille du texte de l’éditeur de code, des différences des modifications et des revues, et de la saisie, en pixels.",
    "Die Textgröße des Code-Editors, der Diffs von Änderungen und Reviews sowie der Eingabe, in Pixeln.",
    "コード エディター、変更とレビューの差分、プロンプトの文字サイズ (ピクセル単位)。",
    "代码编辑器、更改和审阅的差异以及提示词的文字大小，以像素为单位。"],
  "The size of the rest of the text of the window, conversations, lists, menus and dialogs, in percent. The zoom of the window enlarges the text and everything around it.": [
    "El tamaño del resto del texto de la ventana, conversaciones, listas, menús y cuadros de diálogo, en porcentaje. El zoom de la ventana amplía el texto y todo lo que lo rodea.",
    "La taille du reste du texte de la fenêtre, conversations, listes, menus et boîtes de dialogue, en pourcentage. Le zoom de la fenêtre agrandit le texte et tout ce qui l’entoure.",
    "Die Größe des übrigen Textes des Fensters, Unterhaltungen, Listen, Menüs und Dialoge, in Prozent. Der Zoom des Fensters vergrößert den Text und alles um ihn herum.",
    "ウィンドウのその他の文字 (会話、一覧、メニュー、ダイアログ) のサイズ (パーセント単位)。ウィンドウのズームは、文字とその周りのすべてを拡大します。",
    "窗口其余文字（对话、列表、菜单和对话框）的大小，以百分比为单位。窗口缩放会放大文字及其周围的一切。"],
  "The installed fonts could not be listed: type their names.": [
    "No se pudieron enumerar las fuentes instaladas: escribe sus nombres.",
    "Les polices installées n’ont pas pu être listées : saisissez leurs noms.",
    "Die installierten Schriftarten konnten nicht aufgelistet werden: Geben Sie ihre Namen ein.",
    "インストール済みのフォントを一覧にできませんでした。名前を入力してください。",
    "无法列出已安装的字体：请输入字体名称。"],
} as const satisfies Record<string, readonly [string, string, string, string, string]>;

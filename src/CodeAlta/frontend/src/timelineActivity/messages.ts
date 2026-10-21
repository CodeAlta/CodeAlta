// English source keys; translations in es, fr, de, ja, zh-CN order.
export const timelineActivityMessages = {
  // The setting.
  "Focused timeline": ["Cronología enfocada", "Chronologie épurée", "Fokussierter Verlauf", "フォーカスしたタイムライン", "精简时间线"],
  "Fold reasoning, tool calls and background jobs between two messages into one line. Click a line to see its steps.": [
    "Agrupa el razonamiento, las llamadas a herramientas y los trabajos en segundo plano entre dos mensajes en una sola línea. Haz clic en una línea para ver sus pasos.",
    "Regroupe le raisonnement, les appels d’outils et les tâches en arrière-plan entre deux messages sur une seule ligne. Cliquez sur une ligne pour voir ses étapes.",
    "Fasst Überlegungen, Werkzeugaufrufe und Hintergrundaufträge zwischen zwei Nachrichten in einer Zeile zusammen. Klicken Sie auf eine Zeile, um ihre Schritte zu sehen.",
    "2 つのメッセージの間の推論、ツール呼び出し、バックグラウンドジョブを 1 行にまとめます。行をクリックすると手順が表示されます。",
    "将两条消息之间的推理、工具调用和后台作业折叠为一行。单击某行可查看其步骤。"],
  "Images under folded lines": ["Imágenes bajo las líneas agrupadas", "Images sous les lignes repliées", "Bilder unter zusammengefassten Zeilen", "折りたたんだ行の下の画像", "折叠行下方的图片"],
  "Show the images the model read, such as screenshots and image files, under the folded line of its work. Click an image to view it.": [
    "Muestra las imágenes que leyó el modelo, como capturas de pantalla y archivos de imagen, bajo la línea agrupada de su trabajo. Haz clic en una imagen para verla.",
    "Affiche les images lues par le modèle, comme les captures d’écran et les fichiers d’image, sous la ligne repliée de son travail. Cliquez sur une image pour la voir.",
    "Zeigt die vom Modell gelesenen Bilder, etwa Screenshots und Bilddateien, unter der zusammengefassten Zeile seiner Arbeit. Klicken Sie auf ein Bild, um es anzusehen.",
    "スクリーンショットや画像ファイルなど、モデルが読み込んだ画像を作業の折りたたんだ行の下に表示します。画像をクリックすると表示されます。",
    "在其工作的折叠行下方显示模型读取的图片，例如屏幕截图和图片文件。单击图片即可查看。"],
  "{count} image(s) read": ["{count} imágenes leídas", "{count} image(s) lue(s)", "{count} Bild(er) gelesen", "{count} 枚の画像を読み込み", "已读取 {count} 张图片"],
  // The line of an activity.
  "Activity": ["Actividad", "Activité", "Aktivität", "アクティビティ", "活动"],
  "Working…": ["Trabajando…", "Travail en cours…", "Arbeitet…", "作業中…", "正在工作…"],
  "Worked": ["Trabajo realizado", "Travail effectué", "Gearbeitet", "作業済み", "已完成工作"],
  "Calling tools…": ["Llamando a herramientas…", "Appel d’outils…", "Werkzeuge werden aufgerufen…", "ツールを呼び出しています…", "正在调用工具…"],
  "Called tools": ["Herramientas llamadas", "Outils appelés", "Werkzeuge aufgerufen", "ツールを呼び出しました", "已调用工具"],
  "{count} tool call(s)": ["{count} llamadas a herramientas", "{count} appel(s) d’outils", "{count} Werkzeugaufruf(e)", "{count} 回のツール呼び出し", "{count} 次工具调用"],
  "{count} background job(s)": ["{count} trabajos en segundo plano", "{count} tâche(s) en arrière-plan", "{count} Hintergrundauftrag/-aufträge", "{count} 件のバックグラウンドジョブ", "{count} 个后台作业"],
  "{count} thought(s)": ["{count} razonamientos", "{count} réflexion(s)", "{count} Überlegung(en)", "{count} 件の推論", "{count} 段推理"],
  "Latest steps": ["Últimos pasos", "Dernières étapes", "Letzte Schritte", "最新の手順", "最新步骤"],
} as const;

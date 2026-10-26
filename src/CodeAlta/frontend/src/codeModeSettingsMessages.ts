// English source keys; translations in es, fr, de, ja, zh-CN order.
export const codeModeSettingsMessages = {
  "Code Mode": ["Code Mode", "Code Mode", "Code Mode", "Code Mode", "Code Mode"],
  "Code Mode settings unavailable": ["Ajustes de Code Mode no disponibles", "Réglages de Code Mode indisponibles", "Code-Mode-Einstellungen nicht verfügbar", "Code Mode の設定を利用できません", "Code Mode 设置不可用"],
  "Agents write scripts that call their tools, instead of calling them one by one.": [
    "Los agentes escriben scripts que llaman a sus herramientas, en lugar de llamarlas una por una.",
    "Les agents écrivent des scripts qui appellent leurs outils, au lieu de les appeler un par un.",
    "Agenten schreiben Skripte, die ihre Werkzeuge aufrufen, statt sie einzeln aufzurufen.",
    "エージェントはツールを一つずつ呼ぶ代わりに、ツールを呼ぶスクリプトを書きます。",
    "智能体编写调用其工具的脚本，而不是逐个调用工具。"],
  "Agents call their tools one at a time, as usual.": [
    "Los agentes llaman a sus herramientas una a una, como de costumbre.",
    "Les agents appellent leurs outils un à la fois, comme d’habitude.",
    "Agenten rufen ihre Werkzeuge wie gewohnt einzeln auf.",
    "エージェントは通常どおり、ツールを一つずつ呼びます。",
    "智能体照常逐个调用其工具。"],
  "Agents also get exec, which runs JavaScript that calls their tools in loops and in parallel, and wait, for a script that goes on in the background.": [
    "Los agentes también reciben exec, que ejecuta JavaScript que llama a sus herramientas en bucles y en paralelo, y wait, para un script que sigue en segundo plano.",
    "Les agents reçoivent aussi exec, qui exécute du JavaScript appelant leurs outils en boucle et en parallèle, et wait, pour un script qui continue en arrière-plan.",
    "Agenten erhalten zusätzlich exec, das JavaScript ausführt, das ihre Werkzeuge in Schleifen und parallel aufruft, und wait für ein Skript, das im Hintergrund weiterläuft.",
    "エージェントには、ツールをループや並列で呼ぶ JavaScript を実行する exec と、バックグラウンドで続くスクリプトを待つ wait も与えられます。",
    "智能体还会获得 exec（运行可循环、并行调用其工具的 JavaScript）和 wait（等待在后台继续运行的脚本）。"],
  "Only": ["Solo", "Seulement", "Nur", "のみ", "仅"],
  "Agents get exec and wait in place of their tools, which they call from scripts; they still look at images and ask you questions directly.": [
    "Los agentes reciben exec y wait en lugar de sus herramientas, que llaman desde scripts; siguen viendo imágenes y haciéndote preguntas directamente.",
    "Les agents reçoivent exec et wait à la place de leurs outils, qu’ils appellent depuis des scripts ; ils regardent toujours les images et vous posent leurs questions directement.",
    "Agenten erhalten exec und wait anstelle ihrer Werkzeuge, die sie aus Skripten aufrufen; Bilder sehen sie sich weiterhin direkt an und stellen Ihnen Fragen direkt.",
    "エージェントはツールの代わりに exec と wait を受け取り、ツールはスクリプトから呼びます。画像の確認と質問は引き続き直接行います。",
    "智能体获得 exec 和 wait 以代替其工具，并从脚本调用这些工具；它们仍直接查看图片并直接向你提问。"],
  "A session takes the change with its next prompt.": [
    "Una sesión aplica el cambio con su siguiente prompt.",
    "Une session prend le changement avec son prochain prompt.",
    "Eine Sitzung übernimmt die Änderung mit ihrem nächsten Prompt.",
    "セッションは次のプロンプトで変更を反映します。",
    "会话会在下一条提示词时采用此更改。"],
} as const;

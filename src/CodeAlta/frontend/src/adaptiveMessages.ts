// English source keys; translations in es, fr, de, ja, zh-CN order.
export const adaptiveMessages = {
  // Adaptive in the model list of the composer and on Settings > Models.
  "Adaptive": ["Adaptativo", "Adaptatif", "Adaptiv", "アダプティブ", "自适应"],
  "Experimental": ["Experimental", "Expérimental", "Experimentell", "試験的", "实验性"],
  "Not set up": ["Sin configurar", "Non configuré", "Nicht eingerichtet", "未設定", "未设置"],
  "Recheck": ["Revisión", "Vérification", "Nachprüfung", "再確認", "复查"],
  "{model} on trouble": ["{model} si hay problemas", "{model} en cas de problème", "{model} bei Problemen", "問題時は {model}", "出问题时用 {model}"],
  "CodeAlta chooses the model of each turn": ["CodeAlta elige el modelo de cada turno", "CodeAlta choisit le modèle de chaque tour", "CodeAlta wählt das Modell jeder Runde", "CodeAlta がターンごとにモデルを選びます", "CodeAlta 为每一轮选择模型"],
  "Adaptive is not set up": ["Adaptativo no está configurado", "Adaptatif n’est pas configuré", "Adaptiv ist nicht eingerichtet", "アダプティブは未設定です", "自适应尚未设置"],
  "With Adaptive, CodeAlta chooses the model of each turn from your model tiers: a turn that changed code checks its change against the request, and a turn that ends in trouble continues on a stronger model. Choose the tiers it uses in Settings > Models.": [
    "Con Adaptativo, CodeAlta elige el modelo de cada turno entre tus niveles de modelos: un turno que cambió código revisa su cambio frente a la petición, y un turno que termina con problemas continúa con un modelo más potente. Elige los niveles que usa en Configuración > Modelos.",
    "Avec Adaptatif, CodeAlta choisit le modèle de chaque tour parmi vos niveaux de modèles : un tour qui a modifié du code vérifie sa modification par rapport à la demande, et un tour qui se termine mal continue sur un modèle plus fort. Choisissez les niveaux qu’il utilise dans Paramètres > Modèles.",
    "Mit Adaptiv wählt CodeAlta das Modell jeder Runde aus Ihren Modellstufen: Eine Runde, die Code geändert hat, prüft ihre Änderung gegen die Anfrage, und eine Runde, die mit Problemen endet, wird mit einem stärkeren Modell fortgesetzt. Wählen Sie die Stufen in Einstellungen > Modelle.",
    "アダプティブでは、CodeAlta がモデルの階層からターンごとにモデルを選びます。コードを変更したターンは変更を依頼内容と照らして確認し、問題で終わったターンはより強いモデルで続けます。使う階層は 設定 > モデル で選びます。",
    "使用自适应时，CodeAlta 从你的模型层级中为每一轮选择模型：修改了代码的一轮会对照请求复查其改动，以问题结束的一轮会在更强的模型上继续。在“设置 > 模型”中选择它使用的层级。"],
  "Open Settings": ["Abrir Configuración", "Ouvrir les paramètres", "Einstellungen öffnen", "設定を開く", "打开设置"],
  "Adaptive was not chosen ({status}).": ["No se eligió Adaptativo ({status}).", "Adaptatif n’a pas été choisi ({status}).", "Adaptiv wurde nicht gewählt ({status}).", "アダプティブを選べませんでした（{status}）。", "未能选择自适应（{status}）。"],
  "Adaptive was not saved ({status}).": ["Adaptativo no se guardó ({status}).", "Adaptatif n’a pas été enregistré ({status}).", "Adaptiv wurde nicht gespeichert ({status}).", "アダプティブを保存できませんでした（{status}）。", "未能保存自适应（{status}）。"],
  "A session on Adaptive runs its turns on one tier; a turn that changed code checks its change against the request, and a turn that ends in trouble continues once on a stronger tier.": [
    "Una sesión en Adaptativo ejecuta sus turnos en un nivel; un turno que cambió código revisa su cambio frente a la petición, y un turno que termina con problemas continúa una vez en un nivel más potente.",
    "Une session en Adaptatif exécute ses tours sur un niveau ; un tour qui a modifié du code vérifie sa modification par rapport à la demande, et un tour qui se termine mal continue une fois sur un niveau plus fort.",
    "Eine Sitzung auf Adaptiv führt ihre Runden auf einer Stufe aus; eine Runde, die Code geändert hat, prüft ihre Änderung gegen die Anfrage, und eine Runde, die mit Problemen endet, wird einmal auf einer stärkeren Stufe fortgesetzt.",
    "アダプティブのセッションは 1 つの階層でターンを実行します。コードを変更したターンは変更を依頼内容と照らして確認し、問題で終わったターンはより強い階層で 1 回続けます。",
    "自适应会话在一个层级上运行各轮；修改了代码的一轮会对照请求复查其改动，以问题结束的一轮会在更强的层级上继续一次。"],
  "Set up Adaptive": ["Configurar Adaptativo", "Configurer Adaptatif", "Adaptiv einrichten", "アダプティブを設定", "设置自适应"],
  "Turns run on": ["Los turnos usan", "Les tours utilisent", "Runden laufen auf", "ターンの階層", "各轮使用"],
  "A turn in trouble continues on": ["Un turno con problemas continúa en", "Un tour en difficulté continue sur", "Eine Runde mit Problemen läuft weiter auf", "問題のあるターンの続行先", "出问题的一轮继续使用"],
  "Recheck each change of code against the request": ["Revisar cada cambio de código frente a la petición", "Vérifier chaque modification du code par rapport à la demande", "Jede Codeänderung gegen die Anfrage prüfen", "コードの変更を毎回依頼内容と照らして確認する", "对照请求复查每次代码改动"],
  "Add a model to the {tier} tier to use Adaptive.": ["Añade un modelo al nivel {tier} para usar Adaptativo.", "Ajoutez un modèle au niveau {tier} pour utiliser Adaptatif.", "Fügen Sie der Stufe {tier} ein Modell hinzu, um Adaptiv zu verwenden.", "アダプティブを使うには {tier} 階層にモデルを追加してください。", "请向 {tier} 层级添加模型以使用自适应。"],
  "How to choose the models": ["Cómo elegir los modelos", "Comment choisir les modèles", "So wählen Sie die Modelle", "モデルの選び方", "如何选择模型"],
  "Turns: a fast, inexpensive model that writes good code once it rechecks it. Escalation: your strongest model; it runs only when a turn ends in trouble.": [
    "Turnos: un modelo rápido y económico que escribe buen código cuando lo revisa. Escalado: tu modelo más potente; solo se usa cuando un turno termina con problemas.",
    "Tours : un modèle rapide et peu coûteux qui écrit du bon code une fois qu’il le vérifie. Escalade : votre modèle le plus fort ; il ne sert que lorsqu’un tour se termine mal.",
    "Runden: ein schnelles, günstiges Modell, das guten Code schreibt, sobald es ihn nachprüft. Eskalation: Ihr stärkstes Modell; es läuft nur, wenn eine Runde mit Problemen endet.",
    "ターン：再確認すれば良いコードを書ける、速くて安価なモデル。エスカレーション：最も強いモデル。問題で終わったターンでのみ使われます。",
    "各轮：一个快速、便宜、复查后能写出好代码的模型。升级：你最强的模型；只在某轮以问题结束时运行。"],
  "Tested in October 2026: Claude Haiku 5.5 for the turns and Opus 5.5 on trouble came close to Opus alone on real repositories, for about a tenth of its cost. With Codex, choose GPT-6.1 Terra rather than GPT-6 Luna for the turns.": [
    "Probado en octubre de 2026: Claude Haiku 5.5 para los turnos y Opus 5.5 ante problemas se acercó a Opus solo en repositorios reales, por cerca de una décima parte de su coste. Con Codex, elige GPT-6.1 Terra en lugar de GPT-6 Luna para los turnos.",
    "Testé en octobre 2026 : Claude Haiku 5.5 pour les tours et Opus 5.5 en cas de problème s’est approché d’Opus seul sur de vrais dépôts, pour environ un dixième de son coût. Avec Codex, choisissez GPT-6.1 Terra plutôt que GPT-6 Luna pour les tours.",
    "Getestet im Oktober 2026: Claude Haiku 5.5 für die Runden und Opus 5.5 bei Problemen kam auf echten Repositorys nah an Opus allein heran, für etwa ein Zehntel der Kosten. Wählen Sie bei Codex für die Runden GPT-6.1 Terra statt GPT-6 Luna.",
    "2026 年 10 月の検証：ターンに Claude Haiku 5.5、問題時に Opus 5.5 を使うと、実際のリポジトリで Opus 単独に近い結果を約 10 分の 1 のコストで得られました。Codex では、ターンに GPT-6 Luna ではなく GPT-6.1 Terra を選んでください。",
    "2026 年 10 月测试：各轮使用 Claude Haiku 5.5、出问题时使用 Opus 5.5，在真实仓库上接近单独使用 Opus 的效果，成本约为其十分之一。使用 Codex 时，各轮请选择 GPT-6.1 Terra 而不是 GPT-6 Luna。"],
  "To compare other models by their results on coding tasks and their price, see independent leaderboards such as": [
    "Para comparar otros modelos por sus resultados en tareas de programación y su precio, consulta clasificaciones independientes como",
    "Pour comparer d’autres modèles selon leurs résultats sur des tâches de programmation et leur prix, consultez des classements indépendants comme",
    "Um andere Modelle nach ihren Ergebnissen bei Programmieraufgaben und ihrem Preis zu vergleichen, sehen Sie sich unabhängige Ranglisten an, etwa",
    "他のモデルをコーディング課題の結果と価格で比べるには、次のような独立したランキングを参照してください：",
    "要按编程任务结果和价格比较其他模型，可参考独立排行榜，例如"],
  // The titles of the messages Adaptive sends to a session, in its timeline.
  "Adaptive recheck": ["Revisión de Adaptativo", "Vérification d’Adaptatif", "Nachprüfung von Adaptiv", "アダプティブの再確認", "自适应复查"],
  "Adaptive escalation": ["Escalado de Adaptativo", "Escalade d’Adaptatif", "Eskalation von Adaptiv", "アダプティブのエスカレーション", "自适应升级"],
} as const;

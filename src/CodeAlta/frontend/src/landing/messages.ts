// The words of the landing page, in the five languages after English (es, fr, de, ja, zh-CN), merged into the dictionary of
// `localization.ts`. English is the key.
type Row = readonly [string, string, string, string, string];

export const landingMessages = {
  "Welcome": ["Bienvenida", "Bienvenue", "Willkommen", "ようこそ", "欢迎"],
  "Pick up where you left off, or start something new.": ["Continúa donde lo dejaste o empieza algo nuevo.", "Reprenez là où vous vous étiez arrêté, ou commencez autre chose.", "Machen Sie dort weiter, wo Sie aufgehört haben, oder beginnen Sie etwas Neues.", "前回の続きから再開するか、新しい作業を始めましょう。", "从上次中断的地方继续，或开始新的工作。"],
  "Open a project": ["Abrir un proyecto", "Ouvrir un projet", "Projekt öffnen", "プロジェクトを開く", "打开项目"],
  "Documentation": ["Documentación", "Documentation", "Dokumentation", "ドキュメント", "文档"],
  "Get started": ["Primeros pasos", "Premiers pas", "Erste Schritte", "はじめに", "开始使用"],
  "Set up a model provider": ["Configura un proveedor de modelos", "Configurer un fournisseur de modèles", "Modellanbieter einrichten", "モデルプロバイダーを設定", "设置模型提供商"],
  "Sign in to a provider or add an API key, so that sessions can run.": ["Inicia sesión en un proveedor o añade una clave de API para que las sesiones puedan ejecutarse.", "Connectez-vous à un fournisseur ou ajoutez une clé d’API pour que les sessions puissent s’exécuter.", "Melden Sie sich bei einem Anbieter an oder fügen Sie einen API-Schlüssel hinzu, damit Sitzungen laufen können.", "セッションを実行できるように、プロバイダーにサインインするか API キーを追加します。", "登录提供商或添加 API 密钥，会话才能运行。"],
  "Set up providers": ["Configurar proveedores", "Configurer les fournisseurs", "Anbieter einrichten", "プロバイダーを設定", "设置提供商"],
  "Add your first project": ["Añade tu primer proyecto", "Ajouter votre premier projet", "Erstes Projekt hinzufügen", "最初のプロジェクトを追加", "添加第一个项目"],
  "Open the folder of a repository to work on it with an agent.": ["Abre la carpeta de un repositorio para trabajar en él con un agente.", "Ouvrez le dossier d’un dépôt pour y travailler avec un agent.", "Öffnen Sie den Ordner eines Repositorys, um mit einem Agenten daran zu arbeiten.", "リポジトリのフォルダーを開いて、エージェントと作業します。", "打开仓库所在的文件夹，与智能体一起处理它。"],
  "Recent projects": ["Proyectos recientes", "Projets récents", "Zuletzt verwendete Projekte", "最近のプロジェクト", "最近的项目"],
  "No session yet. Send a first prompt to start one.": ["Aún no hay sesiones. Envía un primer mensaje para iniciar una.", "Aucune session pour l’instant. Envoyez un premier prompt pour en démarrer une.", "Noch keine Sitzung. Senden Sie einen ersten Prompt, um eine zu starten.", "セッションはまだありません。最初のプロンプトを送信して開始します。", "还没有会话。发送第一条提示即可开始。"],
  "No project yet. Open a folder to add one.": ["Aún no hay proyectos. Abre una carpeta para añadir uno.", "Aucun projet pour l’instant. Ouvrez un dossier pour en ajouter un.", "Noch kein Projekt. Öffnen Sie einen Ordner, um eines hinzuzufügen.", "プロジェクトはまだありません。フォルダーを開いて追加します。", "还没有项目。打开文件夹即可添加。"],
  "Explore": ["Explorar", "Explorer", "Entdecken", "探す", "探索"],
  "Keyboard shortcuts": ["Atajos de teclado", "Raccourcis clavier", "Tastenkürzel", "キーボードショートカット", "键盘快捷键"],
  "The welcome page: recent sessions and projects, the documentation and the cards of plugins.": ["La página de bienvenida: sesiones y proyectos recientes, la documentación y las tarjetas de los complementos.", "La page de bienvenue : sessions et projets récents, la documentation et les cartes des extensions.", "Die Willkommensseite: zuletzt verwendete Sitzungen und Projekte, die Dokumentation und die Karten der Plugins.", "ようこそページ: 最近のセッションとプロジェクト、ドキュメント、プラグインのカード。", "欢迎页：最近的会话和项目、文档以及插件的卡片。"],
  "Show the welcome page at startup":["Mostrar la página de bienvenida al iniciar", "Afficher la page de bienvenue au démarrage", "Willkommensseite beim Start anzeigen", "起動時にようこそページを表示", "启动时显示欢迎页"],
  "Animate the welcome page": ["Animar la página de bienvenida", "Animer la page de bienvenue", "Willkommensseite animieren", "ようこそページをアニメーション表示", "为欢迎页启用动画"],
  "Show at startup":["Mostrar al iniciar", "Afficher au démarrage", "Beim Start anzeigen", "起動時に表示", "启动时显示"],
  "Animation": ["Animación", "Animation", "Animation", "アニメーション", "动画"],
  "This card could not be loaded.": ["No se pudo cargar esta tarjeta.", "Cette carte n’a pas pu être chargée.", "Diese Karte konnte nicht geladen werden.", "このカードを読み込めませんでした。", "无法加载此卡片。"],
} satisfies Record<string, Row>;

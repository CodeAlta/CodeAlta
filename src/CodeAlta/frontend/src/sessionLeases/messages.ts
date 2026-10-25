// English source keys; translations in es, fr, de, ja, zh-CN order.
export const sessionLeasesMessages = {
  // A session another process holds: its mark, and why its prompt is read-only.
  "Running in altacli": ["En ejecución en altacli", "En cours dans altacli", "Läuft in altacli", "altacli で実行中", "正在 altacli 中运行"],
  "Open in CodeAlta TUI": ["Abierta en CodeAlta TUI", "Ouverte dans CodeAlta TUI", "In CodeAlta TUI geöffnet", "CodeAlta TUI で開いています", "已在 CodeAlta TUI 中打开"],
  "Open in another CodeAlta Desktop": ["Abierta en otro CodeAlta Desktop", "Ouverte dans un autre CodeAlta Desktop", "In einem anderen CodeAlta Desktop geöffnet", "別の CodeAlta Desktop で開いています", "已在另一个 CodeAlta Desktop 中打开"],
  "In use by another process": ["En uso por otro proceso", "Utilisée par un autre processus", "Von einem anderen Prozess verwendet", "別のプロセスが使用中", "正被另一个进程使用"],
  // A rename refused because another process runs the session.
  "Another process runs this session. Rename it once that ends.": ["Otro proceso ejecuta esta sesión. Cámbiale el nombre cuando termine.", "Un autre processus exécute cette session. Renommez-la une fois qu’il a terminé.", "Ein anderer Prozess führt diese Sitzung aus. Benenne sie um, sobald er fertig ist.", "別のプロセスがこのセッションを実行しています。終了してから名前を変更してください。", "另一个进程正在运行此会话。请在其结束后重命名。"],
} as const;

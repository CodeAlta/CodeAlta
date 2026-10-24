// The words of the settings page of the Mobile plugin, in the five languages after English (es, fr, de, ja, zh-CN), merged into the dictionary
// of `localization.ts`. English is the key.
export const mobileSettingsMessages = {
  "Ports": ["Puertos", "Ports", "Ports", "ポート", "端口"],
  "Port on this computer": ["Puerto en este equipo", "Port sur cet ordinateur", "Port auf diesem Computer", "このコンピューターのポート", "此计算机上的端口"],
  "Port on the tailnet": ["Puerto en la tailnet", "Port sur le tailnet", "Port im Tailnet", "tailnet のポート", "tailnet 上的端口"],
  "A port is a number from 1024 to 65535.": ["Un puerto es un número de 1024 a 65535.", "Un port est un nombre de 1024 à 65535.", "Ein Port ist eine Zahl von 1024 bis 65535.",
    "ポートは 1024 から 65535 までの数値です。", "端口是 1024 到 65535 之间的数字。"],
  "The ports could not be changed.": ["No se pudieron cambiar los puertos.", "Les ports n’ont pas pu être changés.", "Die Ports konnten nicht geändert werden.",
    "ポートを変更できませんでした。", "无法更改端口。"],
  "Address on the tailnet": ["Dirección en la tailnet", "Adresse sur le tailnet", "Adresse im Tailnet", "tailnet 上のアドレス", "tailnet 上的地址"],
  "Not served on the tailnet": ["No se sirve en la tailnet", "Non servi sur le tailnet", "Nicht im Tailnet bereitgestellt", "tailnet では提供されていません", "未在 tailnet 上提供"],
  "Access": ["Acceso", "Accès", "Zugriff", "アクセス", "访问"],
  "Access of the phones": ["Acceso de los teléfonos", "Accès des téléphones", "Zugriff der Telefone", "電話のアクセス", "手机的访问"],
  "Reset access": ["Restablecer el acceso", "Réinitialiser l’accès", "Zugriff zurücksetzen", "アクセスをリセット", "重置访问"],
  "Reset access?": ["¿Restablecer el acceso?", "Réinitialiser l’accès ?", "Zugriff zurücksetzen?", "アクセスをリセットしますか？", "要重置访问吗？"],
  "Every phone will need to scan the new code.": ["Cada teléfono tendrá que escanear el nuevo código.", "Chaque téléphone devra scanner le nouveau code.",
    "Jedes Telefon muss den neuen Code scannen.", "すべての電話で新しいコードを読み取る必要があります。", "每部手机都需要扫描新的二维码。"],
  "The access was reset.": ["Se restableció el acceso.", "L’accès a été réinitialisé.", "Der Zugriff wurde zurückgesetzt.", "アクセスをリセットしました。", "访问已重置。"],
  "The access could not be reset.": ["No se pudo restablecer el acceso.", "L’accès n’a pas pu être réinitialisé.", "Der Zugriff konnte nicht zurückgesetzt werden.",
    "アクセスをリセットできませんでした。", "无法重置访问。"],
} as const;

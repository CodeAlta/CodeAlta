// English source keys; translations in es, fr, de, ja, zh-CN order.
export const providerSpaceMessages = {
  // A session whose provider was taken out of its space.
  "{provider} is not available in the space of this session: choose another provider to send.": ["{provider} no está disponible en el espacio de esta sesión: elige otro proveedor para enviar.", "{provider} n’est pas disponible dans l’espace de cette session : choisissez un autre fournisseur pour envoyer.", "{provider} ist im Bereich dieser Sitzung nicht verfügbar: Zum Senden einen anderen Anbieter wählen.", "{provider} はこのセッションのスペースでは使用できません。送信するには別のプロバイダーを選んでください。", "{provider} 在此会话的空间中不可用：请选择其他提供商后再发送。"],
  // A session whose provider was removed from the configuration, or turned off.
  "{provider} is no longer available: choose another provider to send.": ["{provider} ya no está disponible: elige otro proveedor para enviar.", "{provider} n’est plus disponible : choisissez un autre fournisseur pour envoyer.", "{provider} ist nicht mehr verfügbar: Zum Senden einen anderen Anbieter wählen.", "{provider} は使用できなくなりました。送信するには別のプロバイダーを選んでください。", "{provider} 已不再可用：请选择其他提供商后再发送。"],
  // Such a session switched by itself to the default provider.
  "{removed} is no longer configured: this session now uses {provider}.": ["{removed} ya no está configurado: esta sesión usa ahora {provider}.", "{removed} n’est plus configuré : cette session utilise maintenant {provider}.", "{removed} ist nicht mehr eingerichtet: Diese Sitzung verwendet jetzt {provider}.", "{removed} は構成されていません。このセッションは現在 {provider} を使用しています。", "{removed} 已不再配置：此会话现在使用 {provider}。"],
} as const;

using System.Runtime.CompilerServices;
using CodeAlta.LiveTool;

// Keep existing compiled LiveTool consumers bound to the one relocated implementation.
[assembly: TypeForwardedTo(typeof(AltaCallerIdentity))]
[assembly: TypeForwardedTo(typeof(AltaAskRequest))]
[assembly: TypeForwardedTo(typeof(AltaAskQuestion))]
[assembly: TypeForwardedTo(typeof(AltaAskChoice))]
[assembly: TypeForwardedTo(typeof(AltaAskFreeform))]
[assembly: TypeForwardedTo(typeof(AltaReviewTarget))]
[assembly: TypeForwardedTo(typeof(AltaAskAnswer))]
[assembly: TypeForwardedTo(typeof(AltaQueuedAsk))]
[assembly: TypeForwardedTo(typeof(AltaAskQueueResult))]
[assembly: TypeForwardedTo(typeof(AltaAskRemovalResult))]
[assembly: TypeForwardedTo(typeof(IAltaAskService))]
[assembly: TypeForwardedTo(typeof(AltaAskQueueChangedEventArgs))]
[assembly: TypeForwardedTo(typeof(AltaAskService))]
[assembly: TypeForwardedTo(typeof(AltaAskValidator))]
[assembly: TypeForwardedTo(typeof(AltaAskAnswerMarkdownFormatter))]
[assembly: TypeForwardedTo(typeof(AltaAskResponseState))]
[assembly: TypeForwardedTo(typeof(AltaAskResponseHandle))]
[assembly: TypeForwardedTo(typeof(AltaAskResponseResult))]

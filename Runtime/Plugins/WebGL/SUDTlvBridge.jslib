mergeInto(LibraryManager.library, {
  SUD_TlvRegisterCallArena: function(arenaPtr, byteLength) {
    window.SUDTlvGetHeapU8 = function() {
      return HEAPU8;
    };
    return window.SUDTlvBridge.onRegisterCallArena({
      arenaPtr: arenaPtr,
      byteLength: byteLength
    });
  },

  SUD_TlvRegisterJsToCSharpCallbacks: function(requestCallback, eventCallback) {
    window.SUDTlvBridge.unityJslib = {
      dispatchRequestCallback: function(callbackId, callbackIdHigh, callbackType, messageId, frameStartWord) {
        if (!requestCallback) {
          console.error("request callback pointer is missing", {
            callbackId: callbackId,
            callbackType: callbackType,
            messageId: messageId
          });
          return 0;
        }
        {{{ makeDynCall("viiiii", "requestCallback") }}}(
          callbackId | 0,
          callbackIdHigh | 0,
          callbackType | 0,
          messageId | 0,
          frameStartWord);
        return 1;
      },

      dispatchEvent: function(eventToken, messageId, frameStartWord) {
        if (!eventCallback) {
          console.error("event callback pointer is missing", {
            eventToken: eventToken,
            messageId: messageId
          });
          return 0;
        }
        {{{ makeDynCall("viii", "eventCallback") }}}(
          eventToken | 0,
          messageId | 0,
          frameStartWord);
        return 1;
      }
    };
    return 1;
  },

  SUD_TlvDispatchFrame: function(frameStartWord, packedExternalPayloads) {
    return window.SUDTlvBridge.dispatchFrame(
      frameStartWord,
      packedExternalPayloads);
  },

  SUD_TlvCopyExternalPayload: function(externalPayloadId, destinationPtr, byteLength) {
    return window.SUDTlvBridge.copyExternalPayload(
      externalPayloadId,
      destinationPtr,
      byteLength);
  },

  SUD_TlvReleaseExternalPayload: function(externalPayloadId) {
    window.SUDTlvBridge.releaseExternalPayload({
      externalPayloadId: externalPayloadId >>> 0
    });
  }
});

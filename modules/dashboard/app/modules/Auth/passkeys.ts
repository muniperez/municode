/** WebAuthn helpers for the Ory Kratos passkey method; option and credential shapes follow Kratos's webauthn.js. */

type EncodedCredentialDescriptor = { id: string; type: PublicKeyCredentialType; transports?: AuthenticatorTransport[] };
type EncodedOptions = Record<string, unknown> & {
  challenge: string;
  user?: Record<string, unknown> & { id: string };
  allowCredentials?: EncodedCredentialDescriptor[];
  excludeCredentials?: EncodedCredentialDescriptor[];
};

export function passkeysSupported() {
  return typeof window !== "undefined" && typeof window.PublicKeyCredential === "function" &&
    typeof navigator !== "undefined" && typeof navigator.credentials?.get === "function";
}

/** A dismissed, timed-out or aborted browser prompt; the user can retry or use their password. */
const errorName = (error: unknown) =>
  typeof error === "object" && error !== null && "name" in error ? String(error.name) : "";

export function passkeyPromptCanceled(error: unknown) {
  return errorName(error) === "NotAllowedError" || errorName(error) === "AbortError";
}

export function passkeyErrorMessage(error: unknown, operation: "login" | "register") {
  if (passkeyPromptCanceled(error)) {
    return operation === "login" ? "Passkey sign-in was canceled. Try again or sign in with your password." : "Passkey setup was canceled.";
  }
  if (errorName(error) === "InvalidStateError") return "This device already has a passkey for your account.";
  return operation === "login" ? "This device could not sign in with a passkey. Sign in with your password." : "This device could not create a passkey.";
}

export function base64UrlToBuffer(value: string) {
  if (!/^[A-Za-z0-9_-]*={0,2}$/.test(value)) throw new Error("Invalid passkey options.");
  const base64 = value.replace(/=+$/, "").replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(base64 + "=".repeat((4 - base64.length % 4) % 4));
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  return bytes.buffer;
}

export function bufferToBase64Url(value: ArrayBuffer) {
  let binary = "";
  for (const byte of new Uint8Array(value)) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function parseOptions(value: string | null | undefined): EncodedOptions {
  let parsed: unknown;
  try {
    parsed = JSON.parse(value ?? "");
  } catch {
    throw new Error("Invalid passkey options.");
  }
  const publicKey = (parsed as { publicKey?: unknown } | null)?.publicKey;
  if (!publicKey || typeof publicKey !== "object" || typeof (publicKey as EncodedOptions).challenge !== "string") {
    throw new Error("Invalid passkey options.");
  }
  return publicKey as EncodedOptions;
}

const descriptors = (values: EncodedCredentialDescriptor[] | undefined) =>
  values?.map(value => ({ ...value, id: base64UrlToBuffer(value.id) }));

/** Prompts for a passkey and returns the `passkey_login` field value. */
export async function getPasskeyAssertion(challenge: string | null | undefined) {
  const options = parseOptions(challenge);
  const credential = await navigator.credentials.get({
    publicKey: {
      ...options,
      challenge: base64UrlToBuffer(options.challenge),
      allowCredentials: descriptors(options.allowCredentials)
    } as PublicKeyCredentialRequestOptions
  }) as PublicKeyCredential | null;
  if (!credential) throw new DOMException("No passkey was selected.", "NotAllowedError");
  const response = credential.response as AuthenticatorAssertionResponse;
  return JSON.stringify({
    id: credential.id,
    rawId: bufferToBase64Url(credential.rawId),
    type: credential.type,
    response: {
      authenticatorData: bufferToBase64Url(response.authenticatorData),
      clientDataJSON: bufferToBase64Url(response.clientDataJSON),
      signature: bufferToBase64Url(response.signature),
      userHandle: response.userHandle ? bufferToBase64Url(response.userHandle) : undefined
    }
  });
}

/** Prompts the device to create a passkey and returns the `passkey_settings_register` field value. */
export async function createPasskey(createData: string | null | undefined) {
  const options = parseOptions(createData);
  if (!options.user || typeof options.user.id !== "string") throw new Error("Invalid passkey options.");
  const credential = await navigator.credentials.create({
    publicKey: {
      ...options,
      challenge: base64UrlToBuffer(options.challenge),
      user: { ...options.user, id: base64UrlToBuffer(options.user.id) },
      excludeCredentials: descriptors(options.excludeCredentials)
    } as PublicKeyCredentialCreationOptions
  }) as PublicKeyCredential | null;
  if (!credential) throw new DOMException("No passkey was created.", "NotAllowedError");
  const response = credential.response as AuthenticatorAttestationResponse;
  return JSON.stringify({
    id: credential.id,
    rawId: bufferToBase64Url(credential.rawId),
    type: credential.type,
    response: {
      attestationObject: bufferToBase64Url(response.attestationObject),
      clientDataJSON: bufferToBase64Url(response.clientDataJSON)
    }
  });
}

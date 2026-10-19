import { createTokens } from "./core/tokens.js";

const params = new URLSearchParams(location.search);
const tokens = createTokens(params.get("platform") ?? "web");
const root = document.getElementById("app");
root.dataset.platform = tokens.profile;

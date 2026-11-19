// Exposes lit-html to the classic view scripts; runs before DOMContentLoaded.
import { html, render, nothing } from "/lib/lit-html/lit-html.js";
Object.assign(globalThis, { html, render, nothing });

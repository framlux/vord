import adapter from '@sveltejs/adapter-node';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

/** @type {import('@sveltejs/kit').Config} */
const config = {
	kit: {
		adapter: adapter({
			out: 'build'
		}),
		// SvelteKit boots every page with an inline <script>, which a bare script-src 'self' refuses,
		// so the page never hydrates. Letting SvelteKit own the policy means it adds a per-response
		// nonce to the scripts it emits (and to the one in app.html via %sveltekit.nonce%) while
		// scripts stay restricted to this origin. Nothing here is prerendered, so every response
		// carries the policy as a header and frame-ancestors is honoured; a prerendered page could
		// only receive it through a <meta> tag, which ignores frame-ancestors, and X-Frame-Options
		// from the handle hook would then be the only framing protection.
		csp: {
			mode: 'auto',
			directives: {
				'default-src': ['self'],
				'base-uri': ['self'],
				'frame-ancestors': ['none'],
				'img-src': ['self', 'data:'],
				'style-src': ['self', 'unsafe-inline', 'https://fonts.googleapis.com'],
				'font-src': ['self', 'https://fonts.gstatic.com'],
				'script-src': ['self'],
				'connect-src': ['self'],
				'object-src': ['none']
			}
		}
	},
	preprocess: vitePreprocess()
};

export default config;

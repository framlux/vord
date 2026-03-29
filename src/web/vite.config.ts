import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';
import fs from 'node:fs';
import path from 'node:path';

export default defineConfig({
	plugins: [tailwindcss(), sveltekit()],
	server: {
		port: 5173,
		https: {
      		key: fs.readFileSync(path.resolve(import.meta.dirname, '../certs/localhost+1-key.pem')),
      		cert: fs.readFileSync(path.resolve(import.meta.dirname, '../certs/localhost+1.pem')),
    	},
		proxy: {
			'/api': {
				target: 'http://127.0.0.1:12233',
				changeOrigin: true,
				headers: {
					'X-Forwarded-Proto': 'https',
					'X-Forwarded-Host': 'localhost:5173'
				}
			}
		}
	}
});

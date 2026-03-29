import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';
import fs from 'node:fs';
import path from 'node:path';

const keyPath = path.resolve(import.meta.dirname, '../certs/localhost+1-key.pem');
const certPath = path.resolve(import.meta.dirname, '../certs/localhost+1.pem');
const certsExist = fs.existsSync(keyPath) && fs.existsSync(certPath);

export default defineConfig({
	plugins: [tailwindcss(), sveltekit()],
	server: {
		port: 5173,
		https: certsExist
			? {
					key: fs.readFileSync(keyPath),
					cert: fs.readFileSync(certPath)
				}
			: undefined,
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

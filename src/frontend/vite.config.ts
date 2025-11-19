import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default () => {
  process.env = {...process.env, ...loadEnv('', process.cwd())};

  // import.meta.env.VITE_NAME available here with: process.env.VITE_NAME
  // import.meta.env.VITE_PORT available here with: process.env.VITE_PORT

  return defineConfig({
    plugins: [react()],
    assetsInclude: ['**/*.md'],
    server: {
      port: process.env.PORT,
      proxy: {
        '/agent': {
          target: process.env.services__orchestratoragent__https__0 || process.env.services__orchestratoragent__http__0,
        },
      },
    },
  });
}
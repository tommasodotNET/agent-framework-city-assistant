import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default () => {
  process.env = {...process.env, ...loadEnv('', process.cwd())};

  const orchestratorTarget = process.env.services__orchestratoragent__https__0 || process.env.services__orchestratoragent__http__0;
  const voiceTarget = process.env.services__voiceorchestratoragent__https__0 || process.env.services__voiceorchestratoragent__http__0;

  console.log('[vite] Proxy targets:');
  console.log('  /agenta2a ->', orchestratorTarget);
  console.log('  /ws/voice ->', voiceTarget);

  return defineConfig({
    plugins: [react()],
    assetsInclude: ['**/*.md'],
    server: {
      port: process.env.PORT,
      proxy: {
        '/agenta2a': {
          target: orchestratorTarget,
          changeOrigin: true,
          secure: false,
        },
        '/ws/voice': {
          target: voiceTarget,
          changeOrigin: true,
          secure: false,
          ws: true,
        },
      },
    },
  });
}
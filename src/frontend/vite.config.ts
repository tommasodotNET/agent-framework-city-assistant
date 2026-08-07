import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'

export default () => {
  process.env = {...process.env, ...loadEnv('', process.cwd())};

  const classSkillsOrchestratorTarget = process.env.services__orchestratoragent__https__0 || process.env.services__orchestratoragent__http__0;
  const a2aOrchestratorTarget = process.env.services__a2aorchestratoragent__https__0 || process.env.services__a2aorchestratoragent__http__0;
  const voiceTarget = process.env.services__voiceorchestratoragent__https__0 || process.env.services__voiceorchestratoragent__http__0;

  console.log('[vite] Proxy targets:');
  console.log('  /.well-known ->', classSkillsOrchestratorTarget);
  console.log('  /agenta2a ->', classSkillsOrchestratorTarget);
  console.log('  /orchestrators/class-skills ->', classSkillsOrchestratorTarget);
  console.log('  /orchestrators/a2a ->', a2aOrchestratorTarget);
  console.log('  /ws/voice ->', voiceTarget);

  return defineConfig({
    plugins: [react()],
    assetsInclude: ['**/*.md'],
    server: {
      port: process.env.PORT,
      proxy: {
        '/.well-known': {
          target: classSkillsOrchestratorTarget,
          changeOrigin: true,
          secure: false,
        },
        '/agenta2a': {
          target: classSkillsOrchestratorTarget,
          changeOrigin: true,
          secure: false,
        },
        '/orchestrators/class-skills': {
          target: classSkillsOrchestratorTarget,
          changeOrigin: true,
          secure: false,
          rewrite: (path) => path.replace(/^\/orchestrators\/class-skills/, ''),
        },
        '/orchestrators/a2a': {
          target: a2aOrchestratorTarget,
          changeOrigin: true,
          secure: false,
          rewrite: (path) => path.replace(/^\/orchestrators\/a2a/, ''),
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
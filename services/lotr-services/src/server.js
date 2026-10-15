'use strict';
// Entry point: node src/server.js [--config <file>]
// Run as a Windows service by LotRMP.ServiceHost.exe (site deploy package), see DEPLOY.md.
const { loadConfig } = require('./config');
const { createLogger } = require('./log');
const { createApp, loadSupportSecret } = require('./app');

async function main() {
    const config = loadConfig();
    const logger = createLogger({ dir: config.logDir, ...config.log });
    const secret = loadSupportSecret(config);
    if (secret.created) logger.warn('support.secret.created', { file: secret.source, note: 'give this value to the Discord bot (SUPPORT_SERVICE_SECRET or SUPPORT_SERVICE_SECRET_FILE) and restrict the file ACL' });
    const app = createApp(config, { logger, secret: secret.secret });
    await app.start();

    let stopping = false;
    const stop = async (signal) => {
        if (stopping) return;
        stopping = true;
        logger.info('service.stopping', { signal });
        await app.stop();
        process.exit(0);
    };
    process.on('SIGINT', () => stop('SIGINT'));
    process.on('SIGTERM', () => stop('SIGTERM'));
    process.on('SIGBREAK', () => stop('SIGBREAK'));
    process.on('uncaughtException', (error) => {
        logger.error('uncaught', { error: error.stack || error.message });
        app.persistNow();
        process.exit(1); // the service wrapper restarts us; the queue state was just saved
    });
}

main().catch((error) => {
    process.stderr.write(`lotr-services failed to start: ${error.stack || error.message}\n`);
    process.exit(2);
});

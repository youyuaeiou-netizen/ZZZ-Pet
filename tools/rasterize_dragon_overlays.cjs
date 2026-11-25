// Development-only overlay rasterization. The original PNG pixels remain the base.
const fs=require('fs');
const sharp=require('C:/Users/h/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/sharp');
(async()=>{
  for(const job of JSON.parse(fs.readFileSync(process.argv[2],'utf8'))){
    const overlay=await sharp(Buffer.from(job.svg)).png().toBuffer();
    await sharp(job.base).composite([{input:overlay}]).png().toFile(job.target);
  }
})().catch(e=>{console.error(e);process.exitCode=1});

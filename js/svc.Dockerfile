FROM node:10.10.0-alpine as builder
RUN mkdir -p /usr/local/app
RUN apk add --no-cache parallel
RUN echo | parallel --will-cite
ENV PUPPETEER_SKIP_CHROMIUM_DOWNLOAD true

WORKDIR /usr/local/app
COPY ./pkg ./pkg
WORKDIR /usr/local/app/pkg
RUN ls | parallel 'cd {}; npm install --quiet'
RUN ls | parallel 'cd {}; npm test'
RUN ls | parallel 'cd {}; npm install --only=prod --quiet'

WORKDIR /usr/local/app/svc
COPY ./svc/package.json ./package.json
COPY ./svc/yarn.lock ./yarn.lock
RUN yarn install --frozen-lockfile --production=false
COPY ./svc ./
RUN ls -d */ | cut -f1 -d'/' | grep -v '^node_modules$' | parallel 'cd {}; npm test'
RUN yarn install --frozen-lockfile --production=true

FROM node:10.10.0-alpine
ENV NODE_ENV="production"
ENV PORT="80"
EXPOSE 80
RUN mkdir -p /usr/local/app
WORKDIR /usr/local/app
ENTRYPOINT ["sh", "./run.sh"]
COPY ./svc/run.sh .
COPY --from=builder /usr/local/app/pkg ./pkg
COPY --from=builder /usr/local/app/svc ./svc

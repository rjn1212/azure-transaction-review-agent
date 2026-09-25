import { Component, inject, input, OnInit, signal } from '@angular/core';
import { CurrencyPipe, DatePipe, DecimalPipe, JsonPipe, PercentPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { AgentRun, AlertDetail, ApiService, Decision } from '../api';

@Component({
  selector: 'app-alert-detail',
  imports: [RouterLink, CurrencyPipe, DatePipe, DecimalPipe, PercentPipe],
  template: `
    <a routerLink="/" class="back">← Queue</a>

    @if (alert(); as a) {
      <h2>Alert #{{ a.id }} <small class="muted">{{ a.status }}</small></h2>
      <div class="card grid">
        <div><span>Customer</span>{{ a.transaction.customer }}</div>
        <div><span>Merchant</span>{{ a.transaction.merchant }}</div>
        <div><span>Amount</span>{{ a.transaction.amount | currency: a.transaction.currency }}</div>
        <div><span>Where</span>{{ a.transaction.country }} · {{ a.transaction.channel }}</div>
        <div><span>When</span>{{ a.transaction.occurredAt | date: 'medium' }}</div>
        <div><span>Flag reason</span>{{ a.reason }}</div>
        <div><span>Expected (test label)</span>{{ a.expectedDecision ?? '—' }}</div>
      </div>

      <button class="primary" (click)="runAgent()" [disabled]="running()">
        {{ running() ? 'Agent is reviewing…' : 'Run agent' }}
      </button>
      <span class="muted"> Calls OpenAI; about $0.002 per run on gpt-6-luna.</span>
      @if (error()) {
        <p class="error">{{ error() }}</p>
      }

      @for (r of a.runs; track r.id) {
        <div class="card run">
          <div class="run-head">
            <span class="badge big" [class]="r.decision.toLowerCase()">{{ r.decision }}</span>
            <span>confidence {{ r.confidence | percent }}</span>
            <span class="muted">
              {{ r.model }} · {{ r.inputTokens }}/{{ r.outputTokens }} tokens ·
              {{ r.costUsd | currency: 'USD' : 'symbol' : '1.4-6' }} · {{ r.durationMs / 1000 | number: '1.1-1' }}s ·
              {{ r.startedAt | date: 'short' }}
            </span>
          </div>
          <ul>
            @for (reason of r.reasons; track $index) {
              <li>{{ reason }}</li>
            }
          </ul>
          @if (r.policyRules.length) {
            <p class="muted">Rules cited: {{ r.policyRules.join(', ') }}</p>
          }

          <div class="final">
            @if (r.finalDecision) {
              Analyst decision: <span class="badge" [class]="r.finalDecision.toLowerCase()">{{ r.finalDecision }}</span>
              @if (r.finalDecision !== r.decision) { <em>(override)</em> }
            } @else {
              Analyst:
              @for (d of decisions; track d) {
                <button (click)="finalize(r, d)">{{ d === r.decision ? 'Accept ' + d : d }}</button>
              }
            }
          </div>

          <details>
            <summary>Agent trace ({{ r.steps.length }} steps)</summary>
            <ol class="trace">
              @for (s of r.steps; track s.stepNo) {
                <li [class]="s.kind">
                  <strong>{{ s.kind === 'ToolCall' ? '→ call' : s.kind === 'ToolResult' ? '← result' : 'model' }}</strong>
                  <code>{{ s.name }}</code>
                  <pre>{{ pretty(s.payloadJson) }}</pre>
                </li>
              }
            </ol>
          </details>
        </div>
      }
    } @else if (!error()) {
      <p class="muted">Loading…</p>
    }
  `,
})
export class AlertDetailComponent implements OnInit {
  private api = inject(ApiService);
  /** Bound from the :id route param (withComponentInputBinding). */
  id = input.required<string>();
  alert = signal<AlertDetail | null>(null);
  running = signal(false);
  error = signal<string | null>(null);
  decisions: Decision[] = ['APPROVE', 'HOLD', 'ESCALATE'];

  ngOnInit() {
    this.reload();
  }

  reload() {
    this.api.alert(+this.id()).subscribe({
      next: (a) => this.alert.set(a),
      error: (e) => this.error.set(`Could not load alert (${e.status || 'network'}).`),
    });
  }

  runAgent() {
    this.running.set(true);
    this.error.set(null);
    this.api.review(+this.id()).subscribe({
      next: () => {
        this.running.set(false);
        this.reload();
      },
      error: (e) => {
        this.running.set(false);
        this.error.set(
          e.status === 429
            ? (e.error?.detail ?? 'Rate limit reached. Wait a minute and try again.')
            : (e.error?.detail ?? `Agent call failed (${e.status || 'network'}).`),
        );
      },
    });
  }

  finalize(run: AgentRun, d: Decision) {
    this.api.finalize(run.id, d).subscribe({ next: () => this.reload() });
  }

  pretty(json: string): string {
    try {
      const v = JSON.parse(json);
      return typeof v === 'string' ? v : JSON.stringify(v, null, 2);
    } catch {
      return json;
    }
  }
}

import { AfterViewInit, Component, ContentChildren, ElementRef, OnChanges, OnDestroy, OnInit, QueryList, SimpleChanges } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { TextsClient, Text } from 'src/app/autogenerated/texts-client';
import { TranslatorClient } from 'src/app/autogenerated/translator-client';
import { Popover } from "bootstrap";

@Component({
  templateUrl: './view-text.component.html',
  styleUrls: ['./view-text.component.scss']
})
export class ViewTextComponent implements OnInit, OnDestroy {

  text$?: Observable<Text>;
  textId!: string;

  constructor(private textsClient: TextsClient, private translatorClient: TranslatorClient, private route: ActivatedRoute) { }

  ngOnDestroy(): void {
    this.popover?.hide();
  }

  ngOnInit(): void {
    this.textId = this.route.snapshot.params.id;

    this.text$ = this.textsClient.getTextById(this.textId).pipe(
      map(text => {
        text.textContent = this.wrapWords(text.textContent);

        return text;
      })
    );
  }

  wrapWords(text?: string) {
    if (!text) {
      return;
    }

    var pattern = "а-яa-zäöüßûzàèéìíîòóùúzñáéíóúüÁČĎÉĚÍŇÓŘŠŤÚŮÝŽzšžõäöüѓјњќџąćęłńóóśźżăâîșțЂђјљњЋћЏџШшáäčďdžéíĺľňóôŕšťúýžґєії";

    var regexp = new RegExp("[a-zA-z" + pattern + "’'-]+", "gi");

    text = text.replace(regexp, "<span>" + "$&" + "</span>");
    text = text.replace(/\n/g, "<br />");
    
    return text;
  }

  popover!: Popover;
  previousPopoverRef!: HTMLElement | undefined;

  onWordClicked(event: MouseEvent) {

    const currentPopoverRef = event.target as HTMLElement;

    const isWordElement = currentPopoverRef.tagName === 'SPAN';

    if (!isWordElement) {
      this.previousPopoverRef = undefined;
      this.popover?.hide();
      
      return;
    }

    const isRelevantPopoverOpened = currentPopoverRef === this.previousPopoverRef;
    if (isRelevantPopoverOpened) {
      this.popover?.toggle();
      return;
    }

    // hide previous popover
    this.popover?.hide();

    this.previousPopoverRef = currentPopoverRef;

    const word = currentPopoverRef.innerText;

    this.translatorClient.getWordTranslation("en", "ru", word).pipe(
      map(({translations, word }) => {
        const trans = translations?.reduce((accumulator, currentValue) => `${accumulator}<p>${currentValue.translation}</p>`, '');

        return {
          content: trans,
          title: word,
          currentPopoverRef
        }
      })
    ).subscribe(data => this.showPopover(data.content, data.title, data.currentPopoverRef));
  }

  showPopover(content?: string, title?: string, currentPopoverRef?: HTMLElement): void {

    if (!currentPopoverRef) {
      return;
    }

    this.popover = new Popover(currentPopoverRef, {
      container: 'body',
      content: content,
      html: true,
      placement: 'bottom',
      trigger: 'manual',
      title: title
    });

    this.popover.show();
  }

  onClickedOutside(e: Event) {
    this.previousPopoverRef = undefined;
    this.popover?.hide();
  }

}

import { Component, OnInit } from '@angular/core';
import { FormControl, FormGroup } from '@angular/forms';
import { Text, TextsClient } from 'src/app/autogenerated/texts-client';

@Component({
  templateUrl: './text-creation.component.html',
  styleUrls: ['./text-creation.component.scss']
})
export class TextCreationComponent implements OnInit {


  textForm = new FormGroup({
    textName: new FormControl(''),
    text: new FormControl(''),
    inputLanguage: new FormControl('')
  });

  constructor(private textsClient: TextsClient) { }

  ngOnInit(): void {
  }

  onSubmit() {
    this.textsClient.createText(new Text({
      title: this.textForm.get('textName')?.value,
      textContent: this.textForm.get('text')?.value,
      inputLanguage: this.textForm.get('inputLanguage')?.value
    })).subscribe(response => console.log(response));
  }

}
